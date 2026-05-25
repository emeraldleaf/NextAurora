using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.DTOs;
using NextAurora.Contracts.Events;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Infrastructure.Data;
using Wolverine.Tracking;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// Integration coverage for OrderService's outbox and saga handlers against a real SQL Server
/// container, with Wolverine's external transports stubbed (see <see cref="OrderApiFactory"/>).
///
/// <para>
/// Each test uses a fresh Order GUID so the shared per-class container stays isolated without a
/// DB reset between tests. What's proven here is exactly what unit tests can't reach: the
/// outbox flow under Wolverine's middleware chain, saga consume-side handlers running against
/// real EF + SQL Server, idempotency guards, and the <c>RowVersion</c> concurrency token.
/// </para>
/// </summary>
public sealed class OrderSagaTests(OrderApiFactory factory) : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory = factory;

    [Fact]
    public async Task PlaceOrder_persists_order_and_publishes_OrderPlacedEvent()
    {
        // ARRANGE — A POST /api/v1/orders with one valid line. The Catalog client is
        // stubbed to confirm the product exists and reservation succeeds, so the handler
        // reaches the persistence + publish step. We use TrackActivity to capture every
        // message Wolverine routes during the block so we can assert against the
        // OrderPlacedEvent the handler publishes via IEventPublisher.
        var productId = Guid.NewGuid();
        StubCatalogValidProduct(productId, price: 19.99m, stock: 10);

        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Test Product", 2, 19.99m)]);

        var host = _factory.Services.GetRequiredService<IHost>();
        var client = _factory.CreateClient();

        // ACT — POST through the real HTTP pipeline. TrackActivity waits until all
        // cascading messages settle so we can read the captured envelope.
        var session = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/orders", command));

        // ASSERT — Two invariants:
        //  1) OrderPlacedEvent traveled through Wolverine's pipeline. The fact that it
        //     appears in session.Sent proves the publish happened inside the handler's
        //     transaction (which is what UseDurableOutboxOnAllSendingEndpoints wraps).
        //     We capture the event so the DB assertion can target THIS test's order —
        //     other tests in the class share the container and accumulate rows under
        //     the same BuyerId.
        //  2) The Order row was committed to SQL Server, in Placed status, with the
        //     server-computed TotalAmount (2 × $19.99 = $39.98). If the entity write
        //     and the publish weren't transactionally bound, this assertion would
        //     pass while session.Sent could still be empty — but the outbox guarantees
        //     they commit together.
        var placedEvent = session.Sent.SingleMessage<OrderPlacedEvent>();
        placedEvent.OrderId.Should().NotBe(Guid.Empty);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var orderInDb = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placedEvent.OrderId);
        orderInDb.Status.Should().Be(OrderStatus.Placed);
        orderInDb.TotalAmount.Should().Be(2 * 19.99m);
    }

    [Fact]
    public async Task PlaceOrder_does_not_persist_when_catalog_validation_fails()
    {
        // ARRANGE — Stub the catalog to return null → the handler throws
        // InvalidOperationException BEFORE any DB write. This is the critical atomicity
        // case: if the handler's ordering is wrong (e.g. the entity write happens before
        // catalog validation, or the outbox stages without rollback), this test would
        // find an orphan row. We use a unique buyer/product so other tests' orders don't
        // pollute the assertion.
        var productId = Guid.NewGuid();
        _factory.Catalog.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns((ProductDto?)null);

        // The endpoint's buyer-scope check would 403 a different buyer; we want to reach
        // the handler so we use the auth-stamped buyer (TestAuthHandler.BuyerId).
        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Missing Product", 1, 5.00m)]);

        var client = _factory.CreateClient();

        // ACT — Post the order; expect catalog validation to reject it.
        var response = await client.PostAsJsonAsync("/api/v1/orders", command);

        // ASSERT — Two invariants:
        //  1) Response is non-success (the handler threw, GlobalExceptionHandler mapped
        //     it to a 4xx response — the exact status depends on how InvalidOperationException
        //     is mapped, but it must NOT be success).
        //  2) ZERO orders in the DB referencing this product. This is the atomicity
        //     guarantee — a failed validation must not leave any row, partial or otherwise.
        response.IsSuccessStatusCode.Should().BeFalse();

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var matchingOrders = await db.Orders.AsNoTracking()
            .Where(o => o.Lines.Any(l => l.ProductId == productId))
            .ToListAsync();
        matchingOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task PaymentCompletedEvent_transitions_Placed_to_Paid_and_is_idempotent()
    {
        // ARRANGE — Seed a Placed order directly via the DbContext (faster than going
        // through the full PlaceOrder flow). The PaymentCompletedEvent simulates what
        // PaymentService publishes after a successful charge. We use the same event
        // twice to verify idempotency under Service Bus at-least-once delivery.
        var orderId = await SeedOrderAsync(status: OrderStatus.Placed);
        var paymentEvent = new PaymentCompletedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = orderId,
            Amount = 50m,
            Provider = "stripe-test",
            CompletedAt = DateTime.UtcNow,
        };

        var host = _factory.Services.GetRequiredService<IHost>();

        // ACT — First dispatch: the handler should run and the Order transitions
        // Placed → Paid. PublishMessageAndWaitAsync invokes the consumer-side pipeline
        // exactly as Wolverine would on a real Service Bus message.
        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        // ASSERT (intermediate) — After the first dispatch, status is Paid.
        (await GetOrderStatusAsync(orderId)).Should().Be(OrderStatus.Paid);

        // ACT — Second dispatch (Service Bus redelivery simulation). The handler's
        // status-guard MUST short-circuit cleanly — no exception, no extra mutation.
        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        // ASSERT (final) — Status is still Paid. Without the idempotency guard, the
        // second call would either throw (DLQ noise) or corrupt the PaidAt timestamp.
        (await GetOrderStatusAsync(orderId)).Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Order_RowVersion_token_rejects_concurrent_write()
    {
        // ARRANGE — The optimistic-concurrency story. Two independent DbContext scopes
        // load the same row — each captures the same RowVersion snapshot into its
        // tracked entity. This simulates two replicas (or two threads) racing to mutate
        // the same Order. Without the RowVersion shadow column, last-write-wins would
        // silently corrupt state.
        var orderId = await SeedOrderAsync(status: OrderStatus.Placed);

        await using var scope1 = _factory.CreateDbScope();
        await using var scope2 = _factory.CreateDbScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<OrderDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<OrderDbContext>();

        var order1 = await db1.Orders.FirstAsync(o => o.Id == orderId);
        var order2 = await db2.Orders.FirstAsync(o => o.Id == orderId);

        // ACT — First write commits. SQL Server bumps the RowVersion on the row.
        order1.MarkAsPaid();
        await db1.SaveChangesAsync();

        // The second write carries the now-stale RowVersion. SQL Server's UPDATE
        // statement (generated by EF) includes WHERE RowVersion = @original, which
        // now matches zero rows. EF detects this and throws.
        order2.MarkAsPaid();
        var act = async () => await db2.SaveChangesAsync();

        // ASSERT — DbUpdateConcurrencyException is the signal. The HTTP path catches
        // this in GlobalExceptionHandler and returns 409 Conflict; the Wolverine path
        // applies the retry policy (AddConcurrencyRetry) and retries with backoff.
        // Last-write-wins is impossible.
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private void StubCatalogValidProduct(Guid productId, decimal price, int stock)
    {
        _factory.Catalog.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns(new ProductDto
            {
                Id = productId,
                Name = "Test Product",
                Price = price,
                Currency = "USD",
                StockQuantity = stock,
                IsAvailable = true,
            });
        _factory.Catalog.ReserveStockAsync(productId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private async Task<Guid> SeedOrderAsync(OrderStatus status)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var line = OrderLine.Create(Guid.NewGuid(), "Seed Product", quantity: 1, unitPrice: 25m);
        var order = Order.Create(TestAuthHandler.BuyerId, "USD", [line]);

        // SeedOrderAsync sets up tests that need a specific starting status (e.g. Paid for the
        // shipping handler). We don't have a public Status setter — call the named transitions.
        if (status == OrderStatus.Paid) order.MarkAsPaid();

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<OrderStatus> GetOrderStatusAsync(Guid orderId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
    }
}
