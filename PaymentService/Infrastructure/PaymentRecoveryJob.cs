using System.Diagnostics.Metrics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextAurora.Contracts.Events;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;
using Wolverine.EntityFrameworkCore;

namespace PaymentService.Infrastructure;

/// <summary>
/// Recovery sweeper for the classical "stuck Pending payment" failure mode.
///
/// <para>
/// <b>The problem.</b> <c>ProcessPaymentHandler</c> persists a <c>Pending</c> Payment row
/// before calling the external gateway. If the process dies during the gateway call (network
/// blip, k8s pod kill, OOM), the row stays <c>Pending</c> indefinitely — and the per-order
/// idempotency guard (unique index on <c>OrderId</c>) blocks the retry path. Without recovery,
/// the order is stuck.
/// </para>
/// <para>
/// <b>The strategy.</b> This <see cref="BackgroundService"/> wakes every
/// <see cref="PaymentRecoveryOptions.SweepInterval"/>, acquires the distributed
/// <see cref="PaymentRecoveryOptions.LockName"/> lock, finds Payments still in <c>Pending</c>
/// older than <see cref="PaymentRecoveryOptions.StaleThreshold"/>, and marks each Failed.
/// A <see cref="PaymentFailedEvent"/> is published per recovered Payment so OrderService can
/// transition the Order to Failed and NotificationService can tell the buyer to retry.
/// </para>
/// <para>
/// <b>Why mark-Failed rather than gateway-retry.</b> Safer: the gateway interface here doesn't
/// support idempotency keys, so a retry could double-charge. Marking Failed and letting the
/// buyer initiate a fresh checkout is the conservative compensation. If the project later adopts
/// gateway-side idempotency keys, this is the place to layer retry-with-idempotency in.
/// </para>
/// <para>
/// <b>Why a distributed lock.</b> The job runs on every replica. Without coordination, N
/// replicas would each sweep every <see cref="PaymentRecoveryOptions.SweepInterval"/>. The
/// RowVersion concurrency token would prevent double-marking via EF (one winner per row), but
/// the wasted gateway calls + log noise are avoidable. SQL Server's <c>sp_getapplock</c>
/// (via <see cref="DistributedLock.SqlServer"/>) gives us leader election against the existing
/// DB — no new infrastructure. See docs/project-decisions.md §22.
/// </para>
/// <para>
/// <b>Source-generated logging.</b> Every log call goes through a <c>[LoggerMessage]</c>
/// partial method declared at the bottom of the class. This compiles to zero-allocation, fully
/// templated logging that the analyzer (CA1873) is happy with — the alternative would be
/// gating each call with <c>logger.IsEnabled(...)</c> branches, which clutter the read.
/// </para>
/// </summary>
public sealed partial class PaymentRecoveryJob(
    IServiceScopeFactory scopeFactory,
    IDistributedLockProvider lockProvider,
    IOptionsMonitor<PaymentRecoveryOptions> optionsMonitor,
    ILogger<PaymentRecoveryJob> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly Counter<long> RecoveredPayments =
        new Meter("NextAurora").CreateCounter<long>("payments.recovered");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initial = optionsMonitor.CurrentValue;
        LogStarted(logger, initial.SweepInterval, initial.StaleThreshold);

        // First-iteration jitter so multi-replica startups don't all hit the lock at the same
        // millisecond. Cheap, just a few seconds of randomization off the SweepInterval base.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(0, 10)), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Sweep failures must not crash the host; log and continue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(optionsMonitor.CurrentValue.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogStopping(logger);
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var options = optionsMonitor.CurrentValue;

        // TryAcquireAsync with timeout=Zero: if another replica holds the lock right now, we
        // get null and skip this iteration. We'll get another shot at SweepInterval.
        await using var handle = await lockProvider.TryAcquireLockAsync(options.LockName, TimeSpan.Zero, ct);
        if (handle is null)
        {
            LogLockUnavailable(logger);
            return;
        }

        var threshold = timeProvider.GetUtcNow().UtcDateTime - options.StaleThreshold;

        // Stale-payment ID query uses its own short-lived scope: AsNoTracking + projection
        // to just the Guid — no need to load full Payment entities here. We then drop this
        // scope before iterating, because each row gets its own fresh scope below (see the
        // "per-row scope" comment for the rationale).
        List<Guid> staleIds;
        using (var queryScope = scopeFactory.CreateScope())
        {
            var queryContext = queryScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            staleIds = await queryContext.Payments
                .AsNoTracking()
                .Where(p => p.Status == PaymentStatus.Pending && p.CreatedAt < threshold)
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        if (staleIds.Count == 0)
        {
            LogNoStalePayments(logger);
            return;
        }

        LogRecovering(logger, staleIds.Count, threshold);

        // Per-row scope + per-row try/catch. Two reasons:
        //  1. Fresh DbContext per row keeps the change tracker clean. A previous row that
        //     threw mid-SaveChanges can leave entities in a Modified/Detached state that
        //     would poison the next row's save. Each iteration starting from a blank tracker
        //     is the simplest correctness guarantee.
        //  2. One bad row should not crash the sweep. Without the per-row catch, the first
        //     transient failure abandons every subsequent stale Pending until the next
        //     SweepInterval — meaning stuck orders sit longer than necessary.
        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var rowScope = scopeFactory.CreateScope();
                var rowContext = rowScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
                var rowOutbox = rowScope.ServiceProvider.GetRequiredService<IDbContextOutbox>();
                await RecoverOneAsync(id, rowContext, rowOutbox, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                LogRowFailed(logger, ex, id);
            }
            catch (InvalidOperationException ex)
            {
                LogRowFailed(logger, ex, id);
            }
            catch (TimeoutException ex)
            {
                LogRowFailed(logger, ex, id);
            }
        }
    }

    private async Task RecoverOneAsync(Guid paymentId, PaymentDbContext context, IDbContextOutbox outbox, CancellationToken ct)
    {
        var payment = await context.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct);

        // The status check covers two races: (a) ProcessPaymentHandler completed between the
        // ID query and this load, and (b) a previous sweeper iteration already recovered it.
        if (payment is null || payment.Status != PaymentStatus.Pending)
        {
            LogNoLongerPending(logger, paymentId, payment?.Status);
            return;
        }

        // OUTBOX-ATOMIC NON-HANDLER CODE PATH. The sweeper runs OUTSIDE Wolverine's handler pipeline,
        // so AutoApplyTransactions does NOT wrap it. MarkAsFailed (DB write) and PaymentFailedEvent
        // (outbox envelope write) must commit or roll back together — otherwise a crash between them
        // leaves the Payment Failed in-DB with no event enqueued (saga stalls) or, worse under
        // Wolverine 6, the event dispatched before the row commits (a "payment failed" event for a
        // payment that didn't actually persist as failed).
        //
        // We use Wolverine's NON-HANDLER outbox: enroll this DbContext in an IDbContextOutbox, publish
        // through it, then SaveChangesAndFlushMessagesAsync — which stages the envelope, saves the
        // entity, and commits both in one transaction. This is the supported replacement for the old
        // manual BeginTransaction → PublishAsync → SaveChanges → Commit shape, which silently stopped
        // being atomic on 6.x because a constructor-injected IMessageBus (the old IEventPublisher
        // shim) is not enlisted in the transaction and published INLINE. Proven by
        // PaymentRecoveryAtomicityTests. See docs/war-story-wolverine6-outbox-atomicity.md + CLAUDE.md.
        var legacyRow = false;
        try
        {
            outbox.Enroll(context);

            payment.MarkAsFailed("Payment timed out — recovery sweep marked as failed past stale threshold.");

            // Skip the event publish for legacy rows lacking a denormalized BuyerId — these
            // are payments created before the AddBuyerIdToPayment migration and carry
            // Guid.Empty, which downstream consumers won't accept. The MarkAsFailed write
            // still commits as best-effort recovery so an operator can reconcile manually.
            if (payment.BuyerId == Guid.Empty)
            {
                legacyRow = true;
            }
            else
            {
                await outbox.PublishAsync(new PaymentFailedEvent
                {
                    PaymentId = payment.Id,
                    OrderId = payment.OrderId,
                    BuyerId = payment.BuyerId,
                    Reason = "Payment timed out. Please retry checkout.",
                    FailedAt = timeProvider.GetUtcNow().UtcDateTime
                });
            }

            // Atomic: stages the PaymentFailedEvent envelope, saves the MarkAsFailed mutation, and
            // commits both in one transaction. If this throws, nothing is dispatched.
            await outbox.SaveChangesAndFlushMessagesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another process won the RowVersion race. That's fine — the row is either Failed
            // or Completed by whoever raced past us; no further action needed here. The outbox
            // transaction rolls back, so our partial work (and the staged event) doesn't persist.
            LogConcurrencyConflict(logger, ex, paymentId);
            return;
        }

        // Past this point the transaction committed successfully.
        if (legacyRow)
        {
            LogLegacyRow(logger, paymentId);
            RecoveredPayments.Add(1, new KeyValuePair<string, object?>("outcome", "marked-failed-no-event"));
            return;
        }

        RecoveredPayments.Add(1, new KeyValuePair<string, object?>("outcome", "recovered"));
        LogRecovered(logger, paymentId);
    }

    // --- Source-generated logging ------------------------------------------------------------
    // CA1873 (Avoid potentially expensive logging) requires that any log argument that isn't
    // a literal or trivially-cheap expression go through compile-time templated logging. The
    // [LoggerMessage] source generator emits zero-allocation, IsEnabled-guarded implementations
    // of each partial method below, which is the canonical .NET 10 fix.

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "PaymentRecoveryJob started. Sweep interval: {Interval}, stale threshold: {Threshold}")]
    private static partial void LogStarted(ILogger logger, TimeSpan interval, TimeSpan threshold);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PaymentRecoveryJob stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "PaymentRecoveryJob sweep failed; continuing to next iteration")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Another instance holds the recovery lock; skipping this sweep")]
    private static partial void LogLockUnavailable(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "No stale Pending payments to recover")]
    private static partial void LogNoStalePayments(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Recovering {Count} stale Pending payments older than {Threshold:o}")]
    private static partial void LogRecovering(ILogger logger, int count, DateTime threshold);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "Payment {PaymentId} no longer Pending (current: {Status}); skipping")]
    private static partial void LogNoLongerPending(ILogger logger, Guid paymentId, PaymentStatus? status);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug,
        Message = "Payment {PaymentId} updated by another process during sweep; skipping")]
    private static partial void LogConcurrencyConflict(ILogger logger, Exception ex, Guid paymentId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "Payment {PaymentId} has no BuyerId (legacy row); marked Failed but skipping event publish")]
    private static partial void LogLegacyRow(ILogger logger, Guid paymentId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Payment {PaymentId} recovered (Pending → Failed)")]
    private static partial void LogRecovered(ILogger logger, Guid paymentId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "Recovery of payment {PaymentId} failed; continuing to next row in this sweep")]
    private static partial void LogRowFailed(ILogger logger, Exception ex, Guid paymentId);
}
