using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.DTOs;
using NextAurora.Contracts.Events;
using NSubstitute;
using OrderService.Features;
using OrderService.Domain;
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
        var productId = Guid.NewGuid();
        StubCatalogValidProduct(productId, price: 19.99m, stock: 10);

        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Test Product", 2, 19.99m)]);

        var host = _factory.Services.GetRequiredService<IHost>();
        var client = _factory.CreateClient();

        // TrackActivity captures every message Wolverine routes during the block —
        // including the OrderPlacedEvent the handler publishes via IEventPublisher.
        var session = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/orders", command));

        // Wolverine's pipeline saw OrderPlacedEvent travel through it (proves the publish
        // happened inside the handler's transaction, which is what UseDurableOutboxOn
        // AllSendingEndpoints wraps). Capture the event so we can scope the DB assertion
        // to *this* test's order — other tests in the class share the container and
        // accumulate rows under the same BuyerId.
        var placedEvent = session.Sent.SingleMessage<OrderPlacedEvent>();
        placedEvent.OrderId.Should().NotBe(Guid.Empty);

        // And the Order row was committed to SQL Server.
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var orderInDb = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placedEvent.OrderId);
        orderInDb.Status.Should().Be(OrderStatus.Placed);
        orderInDb.TotalAmount.Should().Be(2 * 19.99m);
    }

    [Fact]
    public async Task PlaceOrder_does_not_persist_when_catalog_validation_fails()
    {
        // Stub returns null → handler throws InvalidOperationException before any DB write.
        // If the handler's atomicity is wrong (entity write happens before validation, or the
        // outbox stages without rollback), this test would find an orphan row.
        var productId = Guid.NewGuid();
        _factory.Catalog.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns((ProductDto?)null);

        var buyerId = Guid.NewGuid(); // unique buyer so other tests' orders don't pollute
        var command = new PlaceOrderCommand(
            BuyerId: buyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Missing Product", 1, 5.00m)]);

        // The endpoint's buyer-scope check would 403 this since buyerId != TestAuthHandler.BuyerId;
        // for this test we want to reach the handler, so use the auth-stamped buyer.
        command = command with { BuyerId = TestAuthHandler.BuyerId };

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/orders", command);

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

        // First dispatch: handler runs, status transitions Placed → Paid.
        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        (await GetOrderStatusAsync(orderId)).Should().Be(OrderStatus.Paid);

        // Second dispatch (simulates Service Bus at-least-once redelivery): handler hits the
        // status guard and silently no-ops. No exception, no extra mutation.
        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        (await GetOrderStatusAsync(orderId)).Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Order_RowVersion_token_rejects_concurrent_write()
    {
        var orderId = await SeedOrderAsync(status: OrderStatus.Placed);

        // Two independent scopes load the same row — same RowVersion snapshotted into each.
        await using var scope1 = _factory.CreateDbScope();
        await using var scope2 = _factory.CreateDbScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<OrderDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<OrderDbContext>();

        var order1 = await db1.Orders.FirstAsync(o => o.Id == orderId);
        var order2 = await db2.Orders.FirstAsync(o => o.Id == orderId);

        order1.MarkAsPaid();
        await db1.SaveChangesAsync();

        // Second write carries the now-stale RowVersion. SQL Server's UPDATE matches zero rows
        // (RowVersion filter excludes it) → EF throws. Last-write-wins is impossible.
        order2.MarkAsPaid();
        var act = async () => await db2.SaveChangesAsync();

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
