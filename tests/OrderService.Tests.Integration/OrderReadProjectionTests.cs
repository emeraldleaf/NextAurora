using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Infrastructure.Data;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// Integration coverage for the handler-level read projections (handlers take OrderDbContext directly) —
/// <see cref="GetOrderByIdHandler"/> and
/// <see cref="GetOrdersByBuyerHandler"/>. These were added in the CQRS
/// data-access split (see <c>docs/cqrs-data-access.md</c>); they project to <c>OrderSummaryDto</c>
/// in EF via <c>AsNoTracking().Select(...)</c> with a nested collection projection for the
/// order lines (which triggers EF Core's auto-split behavior — no parent-cartesian rows).
///
/// <para>
/// Unit tests for the corresponding query handlers (<c>GetOrderByIdHandler</c>,
/// <c>GetOrdersByBuyerHandler</c>) mock the handler dependencies (now: OrderDbContext), so the actual EF
/// projection SQL is uncovered there. These tests fill that gap against real SQL Server: a
/// future change to the projection shape (renamed DTO field, broken Lines sub-projection,
/// dropped enum-to-string conversion) surfaces here.
/// </para>
/// <para>
/// Tests share the per-class container from <see cref="OrderApiFactory"/> and use fresh
/// buyer + order GUIDs per test to stay isolated. Seeding happens through a DbContext scope
/// because we're testing the read path in isolation; using the PlaceOrder endpoint would
/// drag in the saga, the catalog stub, and Wolverine — none of which the read path needs.
/// </para>
/// </summary>
public sealed class OrderReadProjectionTests(OrderApiFactory factory) : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory = factory;

    [Fact]
    public async Task GetSummaryByIdAsync_projects_Order_into_OrderSummaryDto_with_lines()
    {
        // ARRANGE — Seed an Order with two lines directly via EF. The projection has to
        // materialize: scalar fields (Id, BuyerId, TotalAmount, PlacedAt), the enum-to-
        // string conversion for Status, AND the nested Lines collection via sub-projection.
        // Two lines proves the collection projection actually iterates; one line would let
        // a hardcoded-single-element bug pass undetected.
        var buyerId = Guid.NewGuid();
        var lines = new List<OrderLine>
        {
            OrderLine.Create(Guid.NewGuid(), "Widget", quantity: 2, unitPrice: 15m),
            OrderLine.Create(Guid.NewGuid(), "Gadget", quantity: 1, unitPrice: 25m)
        };
        var order = Order.Create(buyerId, "USD", lines);
        await SeedOrderAsync(order);

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetOrderByIdHandler>();

        // ACT — Hit the projection method directly. No HTTP, no Wolverine, no cache —
        // just the SQL EF generates for the AsNoTracking().Where(...).Select(...) chain
        // with the nested collection sub-projection.
        var dto = await handler.HandleAsync(new GetOrderByIdQuery(order.Id), CancellationToken.None);

        // ASSERT — Five invariants the projection contract has to hold:
        //  1) Non-null — the row exists and the projection materializes it.
        //  2) Scalar fields round-trip (OrderId, BuyerId, TotalAmount). If a property
        //     got renamed without updating the Select, the value would be wrong/default.
        //  3) Status is the STRING form ("Placed"), not the underlying int. The
        //     projection stringifies the enum so the API never leaks the integer value;
        //     a future regression that drops the stringification fails this assertion.
        //  4) Lines collection has both seeded items — proves the nested sub-projection
        //     ran and EF's auto-split fetched them (rather than dropping them or
        //     materializing only the first row of a cartesian JOIN).
        //  5) Line content round-trips (ProductName + Quantity + UnitPrice). The
        //     projection inside the projection has to work end-to-end.
        dto.Should().NotBeNull();
        dto!.OrderId.Should().Be(order.Id);
        dto.BuyerId.Should().Be(buyerId);
        dto.TotalAmount.Should().Be(2 * 15m + 1 * 25m);
        dto.Status.Should().Be(nameof(OrderStatus.Placed));
        dto.Lines.Should().HaveCount(2);
        dto.Lines.Should().Contain(l => l.ProductName == "Widget" && l.Quantity == 2 && l.UnitPrice == 15m);
        dto.Lines.Should().Contain(l => l.ProductName == "Gadget" && l.Quantity == 1 && l.UnitPrice == 25m);
    }

    [Fact]
    public async Task GetSummaryByIdAsync_returns_null_when_order_does_not_exist()
    {
        // ARRANGE — A random GUID that's never been seeded. The handler relies on null
        // → 404 at the endpoint, so the projection's null-on-missing contract is
        // load-bearing for the API surface.
        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetOrderByIdHandler>();

        // ACT — Project on a non-existent id.
        var dto = await handler.HandleAsync(new GetOrderByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Null, not a default-constructed DTO.
        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetSummariesByBuyerIdAsync_returns_only_the_requested_buyers_orders_newest_first()
    {
        // ARRANGE — Two buyers, three orders for buyer A (different timestamps to verify
        // ordering), one order for buyer B (the leak-check: must NOT appear in buyer A's
        // results). This is the security-critical invariant — a broken WHERE clause
        // would leak buyer B's orders to buyer A, a multi-tenant data-leak class of bug.
        // Using a fresh GUID per buyer keeps tests isolated on the shared container.
        var buyerA = Guid.NewGuid();
        var buyerB = Guid.NewGuid();

        // Three orders for A with intentionally different PlacedAt — the projection
        // applies OrderByDescending(o => o.PlacedAt) so we'll assert they come back
        // newest-first.
        var aOldest = Order.Create(buyerA, "USD", [OrderLine.Create(Guid.NewGuid(), "P1", 1, 10m)]);
        var aMiddle = Order.Create(buyerA, "USD", [OrderLine.Create(Guid.NewGuid(), "P2", 1, 10m)]);
        var aNewest = Order.Create(buyerA, "USD", [OrderLine.Create(Guid.NewGuid(), "P3", 1, 10m)]);
        // Stamp PlacedAt explicitly via SQL after-the-fact (Order.Create defaults it to
        // UtcNow). Done below in SeedAndStampAsync so the three rows have distinct,
        // ordered timestamps independent of seed-call timing.

        var bOrder = Order.Create(buyerB, "USD", [OrderLine.Create(Guid.NewGuid(), "Leak", 1, 99m)]);

        await SeedAndStampAsync(aOldest, placedAt: DateTime.UtcNow.AddMinutes(-30));
        await SeedAndStampAsync(aMiddle, placedAt: DateTime.UtcNow.AddMinutes(-20));
        await SeedAndStampAsync(aNewest, placedAt: DateTime.UtcNow.AddMinutes(-10));
        await SeedAndStampAsync(bOrder, placedAt: DateTime.UtcNow.AddMinutes(-15));

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetOrdersByBuyerHandler>();

        // ACT — Page 1, size 50. Plenty of room for buyer A's three orders.
        var dtos = await handler.HandleAsync(new GetOrdersByBuyerQuery(buyerA, Page: 1, PageSize: 50), CancellationToken.None);

        // ASSERT — Four invariants. Critical: assert against the raw projection result
        // (no .Where pre-filter) so any cross-buyer leak fails the test instead of
        // being silently filtered out by the assertion itself.
        //  1) Exactly three results — proves the WHERE o.BuyerId == buyerId filter is
        //     scoped correctly. A broken filter that returned all buyers would fail
        //     this count check on the full dtos collection (not a pre-filtered view).
        //  2) Every result belongs to buyer A — the OnlyContain shape catches a
        //     broken filter that returned both buyers' orders even if the count
        //     happened to match by coincidence. This is the load-bearing IDOR
        //     assertion: if it ever fails, a multi-tenant data leak is live.
        //  3) Ordering is newest-first by PlacedAt — proves OrderByDescending
        //     survives the projection. Reversing this would break the buyer-facing
        //     "your most recent orders" UX.
        //  4) Each result has its Lines collection populated — proves the nested
        //     sub-projection ran for every parent (EF auto-split on projected
        //     collections, see docs/cqrs-data-access.md).
        dtos.Should().HaveCount(3);
        dtos.Should().OnlyContain(d => d.BuyerId == buyerA);
        dtos.Select(d => d.OrderId).Should().Equal(aNewest.Id, aMiddle.Id, aOldest.Id);
        dtos.Should().OnlyContain(d => d.Lines.Count == 1);
    }

    [Fact]
    public async Task GetSummariesByBuyerIdAsync_paginates_with_Skip_and_Take()
    {
        // ARRANGE — Three orders for a single buyer with distinct timestamps. Page 1
        // size 2 should return the two newest; page 2 size 2 should return the third.
        // If Skip/Take were dropped or misapplied, page 1 would return all three and
        // page 2 would return zero (or duplicate).
        var buyer = Guid.NewGuid();
        var older = Order.Create(buyer, "USD", [OrderLine.Create(Guid.NewGuid(), "P", 1, 5m)]);
        var middle = Order.Create(buyer, "USD", [OrderLine.Create(Guid.NewGuid(), "P", 1, 5m)]);
        var newer = Order.Create(buyer, "USD", [OrderLine.Create(Guid.NewGuid(), "P", 1, 5m)]);
        await SeedAndStampAsync(older, placedAt: DateTime.UtcNow.AddMinutes(-30));
        await SeedAndStampAsync(middle, placedAt: DateTime.UtcNow.AddMinutes(-20));
        await SeedAndStampAsync(newer, placedAt: DateTime.UtcNow.AddMinutes(-10));

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetOrdersByBuyerHandler>();

        // ACT — Walk two pages of size 2.
        var page1 = await handler.HandleAsync(new GetOrdersByBuyerQuery(buyer, Page: 1, PageSize: 2), CancellationToken.None);
        var page2 = await handler.HandleAsync(new GetOrdersByBuyerQuery(buyer, Page: 2, PageSize: 2), CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Page 1 has exactly the two newest — proves Skip(0).Take(2) applied to
        //     the post-OrderByDescending sequence.
        //  2) Page 2 has exactly the single oldest — proves Skip(2).Take(2) walked
        //     past the first page rather than re-fetching from row zero.
        //  3) No overlap between page 1 and page 2 — proves Skip's offset is correct
        //     (a common bug is computing offset as `page * pageSize` instead of
        //     `(page - 1) * pageSize`, which would skip the first page entirely).
        page1.Select(d => d.OrderId).Should().Equal(newer.Id, middle.Id);
        page2.Select(d => d.OrderId).Should().Equal(older.Id);
        page1.Select(d => d.OrderId).Should().NotIntersectWith(page2.Select(d => d.OrderId));
    }

    private async Task SeedOrderAsync(Order order)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds an order and then stamps its <c>PlacedAt</c> to a fixed UTC value via raw
    /// SQL. <c>Order.Create</c> hard-codes <c>PlacedAt = DateTime.UtcNow</c>, which makes
    /// time-ordering assertions flaky when seeding in fast succession. The override
    /// makes the ordering test deterministic without changing the production code path.
    /// </summary>
    private async Task SeedAndStampAsync(Order order, DateTime placedAt)
    {
        await SeedOrderAsync(order);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Orders SET PlacedAt = {placedAt} WHERE Id = {order.Id}");
    }
}
