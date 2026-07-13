using AwesomeAssertions;
using Medallion.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextAurora.Contracts.Events;
using PaymentService.Domain;
using PaymentService.Infrastructure;
using PaymentService.Infrastructure.Data;
using Wolverine.Tracking;
using Xunit;

namespace PaymentService.Tests.Integration;

/// <summary>
/// Proves the transactional-outbox atomicity of the <b>non-handler</b> recovery sweep
/// (<see cref="PaymentRecoveryJob"/>). The sweeper marks a stale Pending payment Failed and publishes
/// <see cref="PaymentFailedEvent"/> inside an explicit transaction; if the publish is properly
/// outbox-staged, a rolled-back commit must dispatch nothing. This is the "outbox-in-non-handler"
/// test CLAUDE.md requires (BackgroundService sweepers / recovery jobs).
/// </summary>
public sealed class PaymentRecoveryAtomicityTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    private readonly PaymentApiFactory _factory = factory;

    [Fact]
    public async Task RecoverySweep_does_not_dispatch_PaymentFailedEvent_when_the_commit_rolls_back()
    {
        // ARRANGE — A per-test host whose PaymentDbContext throws when a Payment is UPDATED, so the
        // recovery sweep's MarkAsFailed commit fails AFTER it has published PaymentFailedEvent.
        // (Adding the seed row is unaffected — the interceptor only throws on Modified entries.)
        await using var rollbackFactory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<PaymentDbContext>>();
                services.AddDbContext<PaymentDbContext>((sp, options) => options
                    .UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("payments-db"))
                    .AddInterceptors(new ThrowOnPaymentUpdateInterceptor()));
            }));

        var sp = rollbackFactory.Services;
        var host = sp.GetRequiredService<IHost>();

        // Seed one Pending payment (fresh — so the *live* 5-minute sweeper ignores it).
        var orderId = Guid.NewGuid();
        Guid paymentId;
        await using (var seedScope = sp.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var payment = Payment.Create(orderId, TestAuthHandler.BuyerId, amount: 42m, currency: "USD", provider: "Stripe");
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            paymentId = payment.Id;
        }

        // A manually-constructed job with a NEGATIVE stale threshold (so the just-seeded row counts
        // as stale) and a UNIQUE lock name (so it never contends with the live hosted sweeper).
        var options = new PaymentRecoveryOptions
        {
            StaleThreshold = TimeSpan.FromMinutes(-5),
            SweepInterval = TimeSpan.FromMinutes(1),
            LockName = "test-recovery-lock-" + Guid.NewGuid().ToString("N"),
        };
        using var job = new PaymentRecoveryJob(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IDistributedLockProvider>(),
            new StaticOptionsMonitor<PaymentRecoveryOptions>(options),
            sp.GetRequiredService<ILogger<PaymentRecoveryJob>>(),
            sp.GetRequiredService<TimeProvider>());

        // ACT — Run one sweep under activity tracking. The MarkAsFailed commit will throw (interceptor);
        // SweepAsync catches it per-row, so no exception escapes. DoNotAssertOnExceptionsDetected guards
        // against the tracked session treating the forced failure as a test error.
        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => job.SweepAsync(CancellationToken.None));

        // ASSERT — Two invariants:
        //  1) The commit rolled back: the payment is still Pending. Also guards the test — if the
        //     interceptor silently didn't apply, the row would be Failed and this fails loudly.
        //  2) ATOMICITY: no PaymentFailedEvent was dispatched. The publish must have been staged in
        //     the rolled-back recovery transaction, not sent inline.
        await using var verifyScope = sp.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var status = await verifyDb.Payments.AsNoTracking()
            .Where(p => p.Id == paymentId).Select(p => p.Status).SingleAsync();
        status.Should().Be(PaymentStatus.Pending,
            "the recovery commit rolled back, so the payment must remain Pending (also confirms the interceptor fired)");

        session.Sent.MessagesOf<PaymentFailedEvent>().Where(e => e.PaymentId == paymentId).Should().BeEmpty(
            "a rolled-back recovery commit must not leave a PaymentFailedEvent dispatched — the publish must be outbox-staged in the recovery transaction, not sent inline");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
