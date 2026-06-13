using System.Net;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.Events;
using ShippingService.Domain;
using ShippingService.Infrastructure.Data;
using Wolverine.Tracking;
using Xunit;

namespace ShippingService.Tests.Integration;

/// <summary>
/// Integration coverage for ShippingService against a real Postgres container with Wolverine's
/// external transports stubbed (see <see cref="ShippingApiFactory"/>).
///
/// <para>
/// What's proven here is exactly what unit tests can't reach: the IDOR-safe SQL predicate on
/// <c>GetShipmentByOrder</c> filtering at the database, the saga consume-side
/// <c>PaymentCompletedHandler</c> creating a Shipment over real EF + Postgres, and idempotency
/// under at-least-once delivery. Each test uses a fresh OrderId so the shared per-class container
/// stays isolated.
/// </para>
/// </summary>
public sealed class ShippingTests(ShippingApiFactory factory) : IClassFixture<ShippingApiFactory>
{
    private readonly ShippingApiFactory _factory = factory;

    [Fact]
    public async Task GetShipmentByOrder_returns_404_for_another_buyers_shipment_IDOR_safe()
    {
        // ARRANGE — Seed a shipment owned by a DIFFERENT buyer (not TestAuthHandler.BuyerId).
        // The endpoint reads NameIdentifier from JWT → passes as RequestingBuyerId → handler
        // pushes the ownership predicate INTO the EF Where clause, so non-owner rows never
        // leave the database. The endpoint translates the null result to 404 (NOT 403 — 403
        // would leak existence). This is the canonical IDOR pattern from CLAUDE.md
        // "Security Requirements → Authorization."
        var otherBuyer = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await SeedShipmentAsync(orderId, otherBuyer);

        var client = _factory.CreateClient();

        // ACT — Authenticated as TestAuthHandler.BuyerId, request the other buyer's shipment.
        var response = await client.GetAsync(new Uri($"/api/v1/shipments/order/{orderId}", UriKind.Relative));

        // ASSERT — 404, not 403. Existence is not leaked. Without this test, the IDOR could
        // survive undetected — exactly the failure mode CLAUDE.md "Testing" warns about.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetShipmentByOrder_returns_shipment_for_owning_buyer()
    {
        // ARRANGE — Seed a shipment owned by the authenticated buyer. The read path projects
        // to a DTO inside the IQueryable (no entity materialization), so this also exercises
        // the projection's compiled SQL against real Postgres.
        var orderId = Guid.NewGuid();
        await SeedShipmentAsync(orderId, TestAuthHandler.BuyerId);

        var client = _factory.CreateClient();

        // ACT — Authenticated buyer requests their own shipment.
        var response = await client.GetAsync(new Uri($"/api/v1/shipments/order/{orderId}", UriKind.Relative));

        // ASSERT — 200, body contains the OrderId. We don't pin the DTO type — the load-bearing
        // assertion is that the right row came back, proving the IDOR-safe predicate worked in
        // the affirmative case and the projection rendered.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(orderId.ToString());
    }

    [Fact]
    public async Task PaymentCompletedHandler_creates_shipment_and_is_idempotent()
    {
        // ARRANGE — PaymentCompletedEvent is what PaymentService publishes after a successful
        // charge. ShippingService's PaymentCompletedHandler is the consume-side saga step
        // that creates the Shipment row. We dispatch twice to verify the idempotency guard
        // under at-least-once delivery — a redelivery must not produce a duplicate Shipment.
        var orderId = Guid.NewGuid();
        var paymentEvent = new PaymentCompletedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = orderId,
            BuyerId = TestAuthHandler.BuyerId,
            Amount = 50m,
            Provider = "stripe-test",
            CompletedAt = DateTime.UtcNow,
        };

        var host = _factory.Services.GetRequiredService<IHost>();

        // ACT — Dispatch the event twice through Wolverine's consumer pipeline. TrackActivity
        // waits until the handler (and any cascaded outbox-staged messages) settle.
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        // ASSERT — Exactly one Shipment for this order. Without the idempotency guard, the
        // second dispatch would either create a duplicate row or throw (DLQ noise) — both
        // visible failure modes here.
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
        var count = await db.Shipments.AsNoTracking().CountAsync(s => s.OrderId == orderId);
        count.Should().Be(1);
    }

    [Fact]
    public async Task ShipmentDispatchedEvent_carries_BuyerId_for_recipient_resolution()
    {
        // ARRANGE — Regression guard for issue #99: ShipmentDispatchedEvent used to lack
        // BuyerId, so NotificationService keyed the "Order Shipped" email to OrderId — an
        // identifier that can never resolve to a real inbox. The buyer's order shipped and
        // they never heard about it. The fix denormalizes BuyerId from the Shipment aggregate
        // (which has carried it since the PR #14 security review) onto the published event,
        // mirroring PaymentCompletedEvent/PaymentFailedEvent.
        var orderId = Guid.NewGuid();
        var buyerId = TestAuthHandler.BuyerId;
        var paymentEvent = new PaymentCompletedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = orderId,
            BuyerId = buyerId,
            Amount = 50m,
            Provider = "stripe-test",
            CompletedAt = DateTime.UtcNow,
        };

        var host = _factory.Services.GetRequiredService<IHost>();

        // ACT — Drive the consume-side saga step end-to-end: PaymentCompletedEvent →
        // PaymentCompletedHandler → CreateShipmentHandler → ShipmentDispatchedEvent.
        // TrackActivity captures everything the cascade publishes.
        var session = await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .PublishMessageAndWaitAsync(paymentEvent);

        // ASSERT — Two invariants on the published event:
        //  1) BuyerId is the BUYER from the originating payment — the identifier downstream
        //     NotificationService resolves to a recipient. Without it the notification path
        //     is silently broken (the bug this test pins).
        //  2) BuyerId is NOT the OrderId — the exact confusion the old placeholder encoded.
        var dispatched = session.Sent.SingleMessage<ShipmentDispatchedEvent>();
        dispatched.BuyerId.Should().Be(buyerId);
        dispatched.BuyerId.Should().NotBe(dispatched.OrderId);
        dispatched.OrderId.Should().Be(orderId);
    }

    private async Task SeedShipmentAsync(Guid orderId, Guid buyerId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();
        var shipment = Shipment.Create(orderId, buyerId, carrier: "USPS");
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
    }
}
