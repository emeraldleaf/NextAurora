using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.Events;
using NSubstitute;
using PaymentService.Domain;
using PaymentService.Features;
using PaymentService.Infrastructure.Data;
using Wolverine.Tracking;
using Xunit;

namespace PaymentService.Tests.Integration;

/// <summary>
/// Integration coverage for PaymentService's Acceptor + Gateway split, outbox, idempotency, and
/// concurrency token against a real SQL Server container with Wolverine's external transports
/// stubbed (see <see cref="PaymentApiFactory"/>).
///
/// <para>
/// Each test uses a fresh OrderId so the shared per-class container stays isolated without a DB
/// reset. What's proven here is exactly what unit tests can't reach: the full
/// <c>ProcessPaymentCommand</c> (Acceptor) → <c>PaymentProcessingRequested</c> (local queue) →
/// <c>PaymentProcessingRequestedHandler</c> (Gateway) cascade, the terminal event staged in the
/// outbox in the same transaction as the state transition, the OrderId-uniqueness idempotency
/// guard, and the <c>RowVersion</c> token — all over real EF + SQL Server.
/// </para>
/// </summary>
public sealed class PaymentProcessingTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    private readonly PaymentApiFactory _factory = factory;

    [Fact]
    public async Task ProcessPayment_drives_Acceptor_to_Gateway_and_completes_with_event()
    {
        // ARRANGE — A POST /api/v1/payments/process for a fresh order. The gateway stub returns
        // success, so the Gateway handler should mark the Payment Completed and publish
        // PaymentCompletedEvent. TrackActivity captures the whole local cascade the HTTP request
        // kicks off: Acceptor persists Pending + publishes PaymentProcessingRequested → Gateway
        // handler consumes it → charges (stub) → publishes PaymentCompletedEvent.
        var orderId = Guid.NewGuid();
        StubGatewaySuccess(transactionId: "stripe_txn_ok");

        var command = new ProcessPaymentCommand(orderId, Amount: 49.98m, Currency: "USD", BuyerId: TestAuthHandler.BuyerId);

        var host = _factory.Services.GetRequiredService<IHost>();
        var client = _factory.CreateClient();

        // ACT — POST through the real pipeline; wait until the cascaded messages settle.
        var session = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/payments/process", command));

        // ASSERT — Two invariants:
        //  1) PaymentCompletedEvent traveled through Wolverine's pipeline — proof the Gateway
        //     handler ran and published inside its transaction (outbox-staged). We read the
        //     event so the DB assertion targets THIS test's payment.
        //  2) The Payment row reached Completed in SQL Server with the gateway's transaction id —
        //     the Acceptor→Gateway split persisted Pending then transitioned to Completed.
        var completed = session.Sent.SingleMessage<PaymentCompletedEvent>();
        completed.OrderId.Should().Be(orderId);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId);
        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.ExternalTransactionId.Should().Be("stripe_txn_ok");
    }

    [Fact]
    public async Task ProcessPayment_marks_Failed_and_publishes_PaymentFailedEvent_on_gateway_decline()
    {
        // ARRANGE — Same flow, but the gateway stub declines. The Gateway handler should mark the
        // Payment Failed and publish PaymentFailedEvent (carrying the raw reason for the audit
        // trail). Fresh order so the per-class container stays isolated.
        var orderId = Guid.NewGuid();
        _factory.Gateway
            .ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(Success: false, TransactionId: "", ErrorMessage: "card_declined"));

        var command = new ProcessPaymentCommand(orderId, Amount: 10m, Currency: "USD", BuyerId: TestAuthHandler.BuyerId);

        var host = _factory.Services.GetRequiredService<IHost>();
        var client = _factory.CreateClient();

        // ACT — Drive the cascade to settlement.
        var session = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/payments/process", command));

        // ASSERT — PaymentFailedEvent published (not Completed), and the row is Failed in the DB.
        var failed = session.Sent.SingleMessage<PaymentFailedEvent>();
        failed.OrderId.Should().Be(orderId);
        failed.Reason.Should().Be("card_declined");

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId);
        payment.Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task ProcessPayment_is_idempotent_per_order_under_duplicate_requests()
    {
        // ARRANGE — Two POSTs for the SAME order id (simulating a double-submit or saga
        // redelivery that reaches the HTTP path). The Acceptor's OrderId existence check must
        // ensure exactly one Payment row is ever created — the second request returns the
        // existing payment and re-publishes its terminal event rather than charging again.
        var orderId = Guid.NewGuid();
        StubGatewaySuccess(transactionId: "stripe_txn_idem");
        var command = new ProcessPaymentCommand(orderId, Amount: 25m, Currency: "USD", BuyerId: TestAuthHandler.BuyerId);

        var host = _factory.Services.GetRequiredService<IHost>();
        var client = _factory.CreateClient();

        // ACT — First request creates + completes the payment; second hits the idempotency guard.
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/payments/process", command));
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/payments/process", command));

        // ASSERT — Exactly one Payment row for this order. Without the existence check (+ the
        // unique OrderId index backstop), the buyer would be charged twice.
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var count = await db.Payments.AsNoTracking().CountAsync(p => p.OrderId == orderId);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Payment_RowVersion_token_rejects_concurrent_write()
    {
        // ARRANGE — Seed a Pending payment, then load it into two independent DbContext scopes.
        // Each captures the same RowVersion snapshot — simulating two replicas racing to mutate
        // the same Payment. Without the token, last-write-wins would silently corrupt state.
        var paymentId = await SeedPendingPaymentAsync();

        await using var scope1 = _factory.CreateDbScope();
        await using var scope2 = _factory.CreateDbScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var p1 = await db1.Payments.FirstAsync(p => p.Id == paymentId);
        var p2 = await db2.Payments.FirstAsync(p => p.Id == paymentId);

        // ACT — First write commits; SQL Server bumps the RowVersion.
        p1.MarkAsCompleted("txn_winner");
        await db1.SaveChangesAsync();

        // The second write carries the now-stale RowVersion. EF's UPDATE ... WHERE RowVersion =
        // @original matches zero rows, and EF throws.
        p2.MarkAsCompleted("txn_loser");
        var act = async () => await db2.SaveChangesAsync();

        // ASSERT — DbUpdateConcurrencyException is the signal. HTTP path → 409; Wolverine path →
        // AddConcurrencyRetry. Last-write-wins is impossible.
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private void StubGatewaySuccess(string transactionId) =>
        _factory.Gateway
            .ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(Success: true, TransactionId: transactionId));

    private async Task<Guid> SeedPendingPaymentAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = Payment.Create(Guid.NewGuid(), TestAuthHandler.BuyerId, amount: 30m, currency: "USD", provider: "Stripe");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }
}
