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
        // Another replica holds the lock.
        AcquireLockFails();

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        await _repository.DidNotReceive().GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenNoStalePayments_PublishesNothing()
    {
        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_MarksStalePaymentFailedAndPublishesEvent()
    {
        var payment = PaymentBuilder.Default().Build();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>()).Returns(payment);

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

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
        // Cutoff = now - StaleThreshold. With now=12:00 UTC and threshold=5min, cutoff=11:55:00.
        var expectedCutoff = _time.GetUtcNow().UtcDateTime - _options.StaleThreshold;

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        await _repository.Received(1).GetStalePendingPaymentIdsAsync(
            Arg.Is<DateTime>(d => d == expectedCutoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenPaymentNoLongerPending_SkipsRecovery()
    {
        // Race: ProcessPaymentHandler completed the payment between the id query and the
        // per-id load. The sweeper must not double-mark.
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsCompleted("txn_xyz");

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>()).Returns(payment);

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenPaymentDeletedBetweenIdAndLoad_SkipsSilently()
    {
        var missingId = Guid.NewGuid();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { missingId });
        _repository.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).ReturnsNull();

        using var job = CreateJob();
        await job.SweepAsync(CancellationToken.None);

        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    // Legacy-row scenario (BuyerId == Guid.Empty for rows created before AddBuyerIdToPayment
    // migration) is not unit-tested: Payment.Create now correctly rejects Guid.Empty as a
    // domain invariant, so the scenario can't be constructed via the factory. The defensive
    // check in PaymentRecoveryJob.RecoverOneAsync is simple enough to verify by inspection;
    // an integration test against a hand-seeded legacy row would be the way to cover it if
    // the path ever needs assertion-level coverage.

    [Fact]
    public async Task Sweep_DisposesLockHandleEvenOnFailure()
    {
        var payment = PaymentBuilder.Default().Build();

        AcquireLockSucceeds();
        _repository.GetStalePendingPaymentIdsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[] { payment.Id });
        _repository.GetByIdAsync(payment.Id, Arg.Any<CancellationToken>())
            .Returns<Payment?>(_ => throw new InvalidOperationException("simulated DB failure"));

        using var job = CreateJob();
        Func<Task> act = () => job.SweepAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await _lockHandle.Received(1).DisposeAsync();
    }
}
