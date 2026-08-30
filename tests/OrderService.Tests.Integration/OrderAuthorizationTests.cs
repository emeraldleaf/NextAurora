using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Infrastructure.Data;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// IDOR-prevention coverage for the buyer-scoped order reads — the buyer-scope variant of the
/// canonical anti-enumeration pattern (see CLAUDE.md "Security Requirements" and
/// docs/cqrs-data-access.md). Per CLAUDE.md "Testing", every endpoint that returns or mutates a
/// scoped entity requires an integration test asserting that a caller cannot read someone else's
/// resource and cannot distinguish "exists but not yours" from "does not exist." Until this class
/// existed, only CatalogService's seller-scope PUT had that test; the order reads had the
/// mechanism (a SQL predicate on <c>BuyerId</c>, null → 404) but nothing pinning it.
///
/// <para>
/// <b>The attack model:</b> an authenticated buyer (JWT subject <see cref="TestAuthHandler.BuyerId"/>)
/// requests an order that belongs to a different buyer. The handler's <c>Where</c> clause filters
/// on both <c>Id</c> and <c>BuyerId</c>, so the non-owner row is never materialized; the endpoint
/// maps the null to 404 (not 403) so the attacker cannot learn whether the order exists.
/// </para>
/// </summary>
public sealed class OrderAuthorizationTests(OrderApiFactory factory) : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory = factory;

    [Fact]
    public async Task GetOrder_byNonOwner_returns404AndRevealsNothing()
    {
        // ARRANGE — Seed an order owned by a buyer who is NOT the JWT principal. The attacker
        // knows (or guesses) the order id; ownership is the only thing standing between them and
        // the order's contents.
        var ownerBuyerId = Guid.NewGuid();
        var orderId = await SeedOrderAsync(ownerBuyerId);
        var client = _factory.CreateClient();

        // ACT — Read the order as the attacker.
        var response = await client.GetAsync(new Uri($"/api/v1/orders/{orderId}", UriKind.Relative));

        // ASSERT — Three invariants:
        //  1) 404, NOT 403 — the anti-enumeration property. A 403 would tell the attacker "this
        //     order exists, just not yours" and let them walk the order-id space.
        //  2) The body carries nothing that identifies the order or its owner. Belt and braces:
        //     the handler's SQL predicate should never have materialized the row at all.
        //  3) The row is untouched and still belongs to the original buyer — a read must not
        //     have side effects, and the assertion goes through the DbContext so nothing
        //     in the HTTP layer can mask it.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a non-owner must get the same answer as for an order that does not exist");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(ownerBuyerId.ToString(), "the response must not leak the owner");
        body.Should().NotContain(orderId.ToString(), "the response must not echo the order id back");
        body.Should().NotContain("Seed Product", "the response must not leak the order's contents");

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var fromDb = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        fromDb.BuyerId.Should().Be(ownerBuyerId);
    }

    [Fact]
    public async Task GetOrder_byOwner_returns200()
    {
        // ARRANGE — The positive control. Without it the 404 above could pass for the wrong
        // reason (a broken route, a failed migration, an auth scheme that rejects everything).
        var orderId = await SeedOrderAsync(TestAuthHandler.BuyerId);
        var client = _factory.CreateClient();

        // ACT — Read the order as its owner.
        var response = await client.GetAsync(new Uri($"/api/v1/orders/{orderId}", UriKind.Relative));

        // ASSERT — 200 with the order in the body proves the scope check is what produced the 404
        // in the non-owner case, not a dead endpoint.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(orderId.ToString(), "the owner must see their own order");
    }

    [Fact]
    public async Task ListOrders_forAnotherBuyer_returns403()
    {
        // ARRANGE — GET /orders/buyer/{buyerId} is scoped by identity, not by resource: the id in
        // the URL is the caller's own buyer id, which they already know. A mismatch is therefore
        // an identity failure (403), not an enumeration risk — there is no resource whose
        // existence a 403 could reveal. This pins that contract so a future refactor doesn't
        // quietly start returning another buyer's list.
        var otherBuyerId = Guid.NewGuid();
        var otherOrderId = await SeedOrderAsync(otherBuyerId);
        var client = _factory.CreateClient();

        // ACT — Ask for someone else's order list.
        var response = await client.GetAsync(new Uri($"/api/v1/orders/buyer/{otherBuyerId}", UriKind.Relative));

        // ASSERT — Forbidden, and no order data in the body.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(otherBuyerId.ToString());
        body.Should().NotContain(otherOrderId.ToString(), "a 403 must not leak which orders exist");
        body.Should().NotContain("Seed Product");
    }

    private async Task<Guid> SeedOrderAsync(Guid buyerId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var line = OrderLine.Create(Guid.NewGuid(), "Seed Product", quantity: 1, unitPrice: 25m);
        var order = Order.Create(buyerId, "USD", [line]);

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }
}
