using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Infrastructure.Data;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// Integration coverage for <see cref="IOrderRepository"/>'s read-side projection methods —
/// <see cref="IOrderRepository.GetSummaryByIdAsync"/> and
/// <see cref="IOrderRepository.GetSummariesByBuyerIdAsync"/>. These were added in the CQRS
/// data-access split (see <c>docs/cqrs-data-access.md</c>); they project to <c>OrderSummaryDto</c>
/// in EF via <c>AsNoTracking().Select(...)</c> with a nested collection projection for the
/// order lines (which triggers EF Core's auto-split behavior — no parent-cartesian rows).
///
/// <para>
/// Unit tests for the corresponding query handlers (<c>GetOrderByIdHandler</c>,
/// <c>GetOrdersByBuyerHandler</c>) mock <see cref="IOrderRepository"/>, so the actual EF
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
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

        // ACT — Hit the projection method directly. No HTTP, no Wolverine, no cache —
        // just the SQL EF generates for the AsNoTracking().Where(...).Select(...) chain
        // with the nested collection sub-projection.
        var dto = await repository.GetSummaryByIdAsync(order.Id);

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
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

        // ACT — Project on a non-existent id.
        var dto = await repository.GetSummaryByIdAsync(Guid.NewGuid());

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
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

        // ACT — Page 1, size 50. Plenty of room for buyer A's three orders.
        var dtos = await repository.GetSummariesByBuyerIdAsync(buyerA, page: 1, pageSize: 50);

        // ASSERT — Four invariants:
        //  1) Exactly buyer A's three orders are returned — proves the WHERE
        //     o.BuyerId == buyerId filter holds. If it were broken, buyer B's order
        //     (or other tests' orders) would leak in.
        //  2) Zero of buyer B's orders appear — the negative half of the leak check.
        //     A WHERE clause built with the wrong operator (== buyerA OR == buyerB,
        //     say) would still pass invariant #1 but fail this one.
        //  3) Ordering is newest-first by PlacedAt — proves the
        //     OrderByDescending(o => o.PlacedAt) survives the projection. Reversing
        //     this would break the buyer-facing "your most recent orders" UX.
        //  4) The returned items are projected DTOs with the Lines collection
        //     populated — proves the nested sub-projection ran for each parent.
        var buyerAOrders = dtos.Where(d => d.BuyerId == buyerA).ToList();
        buyerAOrders.Should().HaveCount(3);
        dtos.Should().NotContain(d => d.BuyerId == buyerB);
        buyerAOrders.Select(d => d.OrderId).Should().Equal(aNewest.Id, aMiddle.Id, aOldest.Id);
        buyerAOrders.Should().OnlyContain(d => d.Lines.Count == 1);
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
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();

        // ACT — Walk two pages of size 2.
        var page1 = await repository.GetSummariesByBuyerIdAsync(buyer, page: 1, pageSize: 2);
        var page2 = await repository.GetSummariesByBuyerIdAsync(buyer, page: 2, pageSize: 2);

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
