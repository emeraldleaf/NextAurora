using AwesomeAssertions;
using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NextAurora.Contracts.Events;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using PaymentService.Domain;
using PaymentService.Infrastructure;
using PaymentService.Tests.Unit.Builders;

namespace PaymentService.Tests.Unit.Application;

/// <summary>
/// Unit tests for <see cref="PaymentRecoveryJob"/>. The tests exercise the sweep logic via
/// the internal <c>SweepAsync</c> method directly, isolating it from the BackgroundService
/// timing loop. A <see cref="FakeTimeProvider"/> controls the "now" clock used for staleness.
/// </summary>
public class PaymentRecoveryJobTests
{
    private readonly IPaymentRepository _repository = Substitute.For<IPaymentRepository>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly IDistributedLockProvider _lockProvider = Substitute.For<IDistributedLockProvider>();
    private readonly IDistributedLock _distributedLock = Substitute.For<IDistributedLock>();
    private readonly IDistributedSynchronizationHandle _lockHandle = Substitute.For<IDistributedSynchronizationHandle>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly PaymentRecoveryOptions _options = new()
    {
        StaleThreshold = TimeSpan.FromMinutes(5),
        SweepInterval = TimeSpan.FromMinutes(1),
        LockName = "payments-recovery"
    };

    public PaymentRecoveryJobTests()
    {
        // The provider's TryAcquireLockAsync(name, timeout, ct) extension dispatches to
        // CreateLock(name).TryAcquireAsync(timeout, ct). Stub the first hop here; each test
        // controls the second hop via AcquireLockSucceeds() / AcquireLockFails().
        _lockProvider.CreateLock(_options.LockName).Returns(_distributedLock);

        // ExecuteInTransactionAsync wraps the recovery work in an EF transaction in production
        // (see PaymentRepository). For unit tests we pass through — the work delegate executes
        // directly so we can assert against the mocked repository/publisher. The transactional
        // semantics themselves are EF's concern, not ours to re-verify here.
        _repository.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var work = callInfo.Arg<Func<CancellationToken, Task>>();
                var ct = callInfo.Arg<CancellationToken>();
                return work(ct);
            });
    }

    private PaymentRecoveryJob CreateJob()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_repository);
        services.AddSingleton(_eventPublisher);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var optionsMonitor = Substitute.For<IOptionsMonitor<PaymentRecoveryOptions>>();
        optionsMonitor.CurrentValue.Returns(_options);

        return new PaymentRecoveryJob(scopeFactory, _lockProvider, optionsMonitor, NullLogger<PaymentRecoveryJob>.Instance, _time);
    }

    private void AcquireLockSucceeds() =>
        _distributedLock.TryAcquireAsync(TimeSpan.Zero, Arg.Any<CancellationToken>())
            .Returns(_ => _lockHandle);

    private void AcquireLockFails() =>
        _distributedLock.TryAcquireAsync(TimeSpan.Zero, Arg.Any<CancellationToken>())
            .Returns(_ => (IDistributedSynchronizationHandle?)null);

    [Fact]
    public async Task Sweep_WhenLockUnavailable_DoesNotQueryRepository()
    {
        // ARRANGE — Multi-replica scenario: another replica holds the payments-recovery
        // distributed lock. This replica's sweep MUST silently no-op. Without this rule,
        // N replicas would each query the stale-pending list, find the same rows, and
        // try to mark them Failed simultaneously — the per-row RowVersion token would
        // still catch the race, but at the cost of N-1 wasted DB round-trips per sweep.
        AcquireLockFails();

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — Repository never queried. Proves the lock check short-circuits BEFORE
        // any DB work — observability stays clean (no per-replica query noise on every tick).
        await _repository.DidNotReceive().GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenNoStalePayments_PublishesNothing()
    {
        // ARRANGE — Happy steady-state: every Pending payment is younger than the stale
        // threshold (normal operation). The sweeper acquires the lock, queries, finds
        // nothing, and exits without publishing.
        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — No PaymentFailedEvent published. A test that asserted "publish was
        // called with empty arg" would mask a bug where the sweep accidentally publishes
        // a default-constructed event; DidNotReceive() is the correct contract.
        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_MarksStalePaymentFailedAndPublishesEvent()
    {
        // ARRANGE — A real Payment is stale (created more than 5 min ago, still Pending —
        // the process that started the gateway call died before MarkAsCompleted/Failed).
        // The sweep must transition it to Failed and publish PaymentFailedEvent so
        // OrderService can mark the order PaymentFailed and tell the buyer.
        var payment = PaymentBuilder.Default().Build();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>()).Returns(payment);

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Domain state transitioned to Failed (proves we went through MarkAsFailed,
        //     not a direct setter — the status guard is intact).
        //  2) UpdateAsync was called to persist the transition.
        //  3) PaymentFailedEvent was published with the right ids — this is the saga
        //     fan-out. BuyerId must carry over so NotificationService can reach the buyer
        //     without a callback into OrderService (denormalized to avoid the round-trip).
        payment.Status.Should().Be(PaymentStatus.Failed);
        await _repository.Received(1).UpdateAsync(payment, Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<PaymentFailedEvent>(e =>
                e.PaymentId == payment.Id &&
                e.OrderId == payment.OrderId &&
                e.BuyerId == payment.BuyerId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_UsesConfiguredStaleThresholdAsCutoff()
    {
        // ARRANGE — Cutoff = now - StaleThreshold. With now=12:00 UTC and threshold=5min,
        // cutoff = 11:55:00. The repository's GetStalePendingPaymentIdsAsync uses the
        // cutoff as a WHERE clause (CreatedAt < @cutoff). If the cutoff is computed
        // wrong, the sweeper either misses real stale rows (waits too long) OR
        // prematurely fails rows that just started their gateway call (eats live charges).
        var expectedCutoff = _time.GetUtcNow().UtcDateTime - _options.StaleThreshold;

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — The cutoff passed to the repository exactly matches now - threshold.
        // FakeTimeProvider makes "now" deterministic so the equality assertion is stable.
        await _repository.Received(1).GetStalePendingPaymentIdsAsync(
            Arg.Is<DateTime>(d => d == expectedCutoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenPaymentNoLongerPending_SkipsRecovery()
    {
        // ARRANGE — Race scenario: the per-id query returned the payment id (it WAS
        // Pending then), but by the time the per-id load runs, ProcessPaymentHandler
        // has completed the payment. The sweeper must NOT double-mark — MarkAsFailed on
        // a Completed payment would corrupt state. The defensive check in RecoverOneAsync
        // catches this.
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsCompleted("txn_xyz");

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>()).Returns(payment);

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) No UpdateAsync — the Completed payment is left alone.
        //  2) No PaymentFailedEvent — without this guard, a happy-path payment would
        //     trigger a spurious "your payment failed" email to the buyer.
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenPaymentDeletedBetweenIdAndLoad_SkipsSilently()
    {
        // ARRANGE — Race scenario: an admin deletes a stuck Payment between the id-query
        // and the per-id load. The sweeper's per-id load returns null. Silent skip is
        // correct — throwing would land on the DLQ for an operationally fine outcome.
        var missingId = Guid.NewGuid();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { missingId });
        _repository.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).ReturnsNull();

        // ACT — Run a single sweep.
        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        // ASSERT — No event published. The sweep continues to the next row (if any).
        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// Why no test for the legacy-row scenario: rows created before the AddBuyerIdToPayment
    /// migration could have an empty BuyerId. Payment.Create now rejects an empty BuyerId at
    /// the domain factory, so the scenario cannot be constructed through normal means in a
    /// unit test. The defensive guard inside PaymentRecoveryJob.RecoverOneAsync is simple
    /// enough to verify by inspection; an integration test seeded directly via raw SQL would
    /// be the way to cover it if assertion-level coverage is ever needed.
    /// </remarks>
    [Fact]
    public async Task Sweep_DisposesLockHandleEvenOnFailure()
    {
        // ARRANGE — Mid-sweep DB failure (network drop, deadlock victim). The lock handle
        // MUST still be disposed — otherwise the next sweep tick on this replica would
        // immediately fail to re-acquire and the recovery path would stall until the
        // process restarted. Acquired with `await using` upstream so this should "just
        // work" — this test pins the contract.
        var payment = PaymentBuilder.Default().Build();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>())
            .Returns<Payment?>(_ => throw new InvalidOperationException("simulated DB failure"));

        // ACT — The DB failure propagates up (sweep failures DO throw — they signal that
        // the recovery path isn't working and Wolverine/host logging should surface it).
        using var job = CreateJob();
        Func<Task> act = () => job.SweepAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // ASSERT — Despite the throw, DisposeAsync ran exactly once — the lock is released.
        await _lockHandle.Received(1).DisposeAsync();
    }
}
