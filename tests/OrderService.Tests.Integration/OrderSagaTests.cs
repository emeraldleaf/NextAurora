using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task PlaceOrder_does_not_dispatch_OrderPlacedEvent_when_the_commit_rolls_back()
    {
        // ARRANGE — The transactional-outbox atomicity guarantee, proven the only conclusive way:
        // force the handler's SaveChanges to fail AFTER it publishes OrderPlacedEvent, and assert
        // the event was NOT dispatched. ThrowingSaveChangesInterceptor throws when the Order row is
        // committed (simulating a crash/constraint at commit). If the publish was correctly
        // outbox-staged in the handler's transaction, the rollback discards the staged envelope and
        // nothing is sent; if it fired inline (the Wolverine 6 constructor-IMessageBus trap that
        // broke PaymentService's local continuation), the event survives the rollback — a broken
        // outbox. This is the verification gap called out in the Wolverine 5→6 upgrade follow-up.
        //
        // A per-test host variant (WithWebHostBuilder) adds the interceptor; it reuses the shared
        // SQL container via the inherited connection-string setting.
        var productId = Guid.NewGuid();
        StubCatalogValidProduct(productId, price: 19.99m, stock: 10);

        // EF Core does not auto-apply a DI-registered interceptor — it must be attached to the
        // DbContext options. Re-register OrderDbContext with the same connection string plus the
        // interceptor; Wolverine's EF-transaction bridge still resolves the same context type.
        await using var rollbackFactory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OrderDbContext>>();
                services.AddDbContext<OrderDbContext>((sp, options) => options
                    .UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("orders-db"))
                    .AddInterceptors(new ThrowingSaveChangesInterceptor()));
            }));

        var host = rollbackFactory.Services.GetRequiredService<IHost>();
        var client = rollbackFactory.CreateClient();

        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Rollback Test Product", 1, 19.99m)]);

        // ACT — POST; the handler publishes OrderPlacedEvent, then SaveChanges throws → the request
        // fails. DoNotAssertOnExceptionsDetected: the forced failure is the point, not a test error.
        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(_ => client.PostAsJsonAsync("/api/v1/orders", command));

        // ASSERT — Two invariants:
        //  1) The interceptor fired and the transaction rolled back — no Order row persisted. This
        //     also guards the test: if the interceptor silently didn't apply, an order WOULD exist
        //     and this fails loudly (no false pass).
        //  2) ATOMICITY: no OrderPlacedEvent was dispatched. The publish must have been staged in
        //     the rolled-back transaction, not sent inline. This is the load-bearing assertion.
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var orphanExists = await db.Orders.AsNoTracking()
            .AnyAsync(o => o.Lines.Any(l => l.ProductId == productId));
        orphanExists.Should().BeFalse(
            "the commit rolled back, so no Order row may persist (also confirms the interceptor fired)");

        session.Sent.MessagesOf<OrderPlacedEvent>().Should().BeEmpty(
            "a rolled-back commit must not leave an OrderPlacedEvent dispatched — the publish must be outbox-staged in the handler transaction, not sent inline");
    }

    [Fact]
    public async Task PlaceOrder_does_not_persist_when_catalog_validation_fails()
    {
        // ARRANGE — Stub the catalog batch-validate to return an empty list (the requested
        // product is absent = "not found") → the handler throws InvalidOperationException
        // BEFORE any DB write. This is the critical atomicity case: if the handler's
        // ordering is wrong (e.g. the entity write happens before catalog validation, or
        // the outbox stages without rollback), this test would find an orphan row. We use
        // a unique buyer/product so other tests' orders don't pollute the assertion.
        var productId = Guid.NewGuid();
        _factory.Catalog.ValidateLinesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([]);

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
    public async Task PlaceOrder_with_five_lines_makes_exactly_one_validate_and_one_reserve_call()
    {
        // ARRANGE — The batch-gRPC contract (issue #71): order placement must cost ONE
        // ValidateLines call + ONE ReserveLines call regardless of order size. The previous
        // shape made N GetProduct + N ReserveStock calls (10 round-trips for this order);
        // this test pins the collapse to 2 and fails if a refactor reintroduces per-line
        // fan-out. ClearReceivedCalls isolates the count from other tests sharing the
        // class-level substitute.
        _factory.Catalog.ClearReceivedCalls();
        var products = Enumerable.Range(0, 5)
            .Select(_ => (ProductId: Guid.NewGuid(), Price: 10m, Stock: 10))
            .ToList();
        StubCatalogValidProducts(products);

        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: products.Select(p => new PlaceOrderLineItem(p.ProductId, "Test Product", 1, p.Price)).ToList());

        var client = _factory.CreateClient();

        // ACT — POST the 5-line order through the real HTTP pipeline.
        var response = await client.PostAsJsonAsync("/api/v1/orders", command);

        // ASSERT — Three invariants:
        //  1) The order succeeded (the batch responses satisfied validation + reservation).
        //  2) Exactly ONE ValidateLinesAsync call — not one per line.
        //  3) Exactly ONE ReserveLinesAsync call, carrying all 5 lines — not one per line.
        response.IsSuccessStatusCode.Should().BeTrue();
        await _factory.Catalog.Received(1)
            .ValidateLinesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 5), Arg.Any<CancellationToken>());
        await _factory.Catalog.Received(1)
            .ReserveLinesAsync(Arg.Is<IReadOnlyCollection<CatalogReserveLine>>(l => l.Count == 5), Arg.Any<CancellationToken>());
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
        => StubCatalogValidProducts([(productId, price, stock)]);

    private void StubCatalogValidProducts(IReadOnlyList<(Guid ProductId, decimal Price, int Stock)> products)
    {
        var dtos = products.Select(p => new ProductDto
        {
            Id = p.ProductId,
            Name = "Test Product",
            Price = p.Price,
            Currency = "USD",
            StockQuantity = p.Stock,
            IsAvailable = true,
        }).ToList();

        _factory.Catalog.ValidateLinesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(dtos);
        _factory.Catalog.ReserveLinesAsync(Arg.Any<IReadOnlyCollection<CatalogReserveLine>>(), Arg.Any<CancellationToken>())
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
