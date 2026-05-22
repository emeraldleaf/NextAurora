using System.Diagnostics.Metrics;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextAurora.Contracts.Events;
using PaymentService.Domain;

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
/// </summary>
public sealed class PaymentRecoveryJob(
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
        logger.LogInformation("PaymentRecoveryJob started. Sweep interval: {Interval}, stale threshold: {Threshold}",
            optionsMonitor.CurrentValue.SweepInterval, optionsMonitor.CurrentValue.StaleThreshold);

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
                logger.LogError(ex, "PaymentRecoveryJob sweep failed; continuing to next iteration");
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

        logger.LogInformation("PaymentRecoveryJob stopping");
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var options = optionsMonitor.CurrentValue;

        // TryAcquireAsync with timeout=Zero: if another replica holds the lock right now, we
        // get null and skip this iteration. We'll get another shot at SweepInterval.
        await using var handle = await lockProvider.TryAcquireLockAsync(options.LockName, TimeSpan.Zero, ct);
        if (handle is null)
        {
            logger.LogDebug("Another instance holds the recovery lock; skipping this sweep");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var threshold = timeProvider.GetUtcNow().UtcDateTime - options.StaleThreshold;
        var staleIds = await repository.GetStalePendingPaymentIdsAsync(threshold, ct);

        if (staleIds.Count == 0)
        {
            logger.LogDebug("No stale Pending payments to recover");
            return;
        }

        logger.LogInformation("Recovering {Count} stale Pending payments older than {Threshold:o}", staleIds.Count, threshold);

        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();
            await RecoverOneAsync(id, repository, eventPublisher, ct);
        }
    }

    private async Task RecoverOneAsync(Guid paymentId, IPaymentRepository repository, IEventPublisher eventPublisher, CancellationToken ct)
    {
        var payment = await repository.GetByIdAsync(paymentId, ct);

        // The status check covers two races: (a) ProcessPaymentHandler completed between the
        // ID query and this load, and (b) a previous sweeper iteration already recovered it.
        if (payment is null || payment.Status != PaymentStatus.Pending)
        {
            logger.LogDebug("Payment {PaymentId} no longer Pending (current: {Status}); skipping", paymentId, payment?.Status);
            return;
        }

        // Outbox atomicity: MarkAsFailed (DB write) and PaymentFailedEvent (outbox row write) must
        // commit or roll back together. Without this transaction, a crash between SaveChanges and
        // PublishAsync would leave the Payment Failed in-DB but the event never enqueued — the
        // saga would stall. The sweeper runs outside Wolverine's handler pipeline so it doesn't
        // get AutoApplyTransactions; we wrap it explicitly here. Wolverine's
        // UseEntityFrameworkCoreTransactions() bridges IMessageBus into the ambient EF tx.
        var legacyRow = false;
        try
        {
            await repository.ExecuteInTransactionAsync(async (txCt) =>
            {
                payment.MarkAsFailed("Payment timed out — recovery sweep marked as failed past stale threshold.");
                await repository.UpdateAsync(payment, txCt);

                // Skip the event publish for legacy rows lacking a denormalized BuyerId — these
                // are payments created before the AddBuyerIdToPayment migration and carry
                // Guid.Empty, which downstream consumers won't accept. The MarkAsFailed write
                // still commits as best-effort recovery so an operator can reconcile manually.
                if (payment.BuyerId == Guid.Empty)
                {
                    legacyRow = true;
                    return;
                }

                await eventPublisher.PublishAsync(new PaymentFailedEvent
                {
                    PaymentId = payment.Id,
                    OrderId = payment.OrderId,
                    BuyerId = payment.BuyerId,
                    Reason = "Payment timed out. Please retry checkout.",
                    FailedAt = timeProvider.GetUtcNow().UtcDateTime
                }, txCt);
            }, ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another process won the RowVersion race. That's fine — the row is either Failed
            // or Completed by whoever raced past us; no further action needed here. Transaction
            // rolls back on dispose so our partial work doesn't persist.
            logger.LogDebug(ex, "Payment {PaymentId} updated by another process during sweep; skipping", paymentId);
            return;
        }

        // Past this point the transaction committed successfully.
        if (legacyRow)
        {
            logger.LogWarning("Payment {PaymentId} has no BuyerId (legacy row); marked Failed but skipping event publish", paymentId);
            RecoveredPayments.Add(1, new KeyValuePair<string, object?>("outcome", "marked-failed-no-event"));
            return;
        }

        RecoveredPayments.Add(1, new KeyValuePair<string, object?>("outcome", "recovered"));
        logger.LogInformation("Payment {PaymentId} recovered (Pending → Failed)", paymentId);
    }
}
