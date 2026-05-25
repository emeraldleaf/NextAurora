using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using ShippingService.Domain;
using ShippingService.Features;

namespace ShippingService.Tests.Unit.Application;

public class GetShipmentByOrderHandlerTests
{
    private readonly IShipmentRepository _repository = Substitute.For<IShipmentRepository>();
    private readonly GetShipmentByOrderHandler _sut;

    public GetShipmentByOrderHandlerTests()
    {
        _sut = new GetShipmentByOrderHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenOwnerRequests_ReturnsShipmentDto()
    {
        // ARRANGE — A real Shipment owned by `buyerId`. The same buyer ID is passed as
        // RequestingBuyerId (filled by the endpoint from the JWT subject claim).
        var buyerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipment = Shipment.Create(orderId, buyerId, "FedEx");
        shipment.Dispatch(); // gives us at least one tracking event to verify mapping
        _repository.GetByOrderIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(shipment);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(
            new GetShipmentByOrderQuery(orderId, buyerId), CancellationToken.None);

        // ASSERT — Five invariants:
        //  1) Non-null — the shipment was found and the caller owns it.
        //  2) OrderId round-trips so the caller can cross-reference.
        //  3) Carrier + tracking number flow through (these are the user-facing fields).
        //  4) Status is the STRING form (the endpoint MUST NOT expose the enum integer).
        //  5) TrackingEvents are mapped into TrackingEventDtos — proves the nested
        //     projection isn't dropped (a common refactor mistake).
        result.Should().NotBeNull();
        result!.OrderId.Should().Be(orderId);
        result.Carrier.Should().Be("FedEx");
        result.TrackingNumber.Should().StartWith("NVC-");
        result.Status.Should().Be(nameof(ShipmentStatus.Dispatched));
        result.TrackingEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenShipmentNotFound_ReturnsNull()
    {
        // ARRANGE — The order may not have reached the shipping stage yet (payment still
        // pending) or never will (payment failed). Null is the unambiguous "no shipment".
        _repository.GetByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(
            new GetShipmentByOrderQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // ASSERT — null translates to a 404 at the endpoint.
        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenRequestingBuyerIsNotOwner_ReturnsNullToHideExistence()
    {
        // ARRANGE — The IDOR-prevention path (CLAUDE.md "Security Requirements" canonical
        // pattern). A different authenticated buyer requests someone else's shipment by
        // guessing/scraping the order ID. The handler MUST NOT distinguish "exists but
        // not yours" from "doesn't exist" — both return null → 404. Returning 403 would
        // leak existence ("there IS a shipment, just not yours") and let an attacker
        // enumerate the order/shipment ID space.
        var ownerBuyerId = Guid.NewGuid();
        var attackerBuyerId = Guid.NewGuid();
        var shipment = Shipment.Create(Guid.NewGuid(), ownerBuyerId, "FedEx");
        _repository.GetByOrderIdAsync(shipment.OrderId, Arg.Any<CancellationToken>())
            .Returns(shipment);

        // ACT — attackerBuyerId is NOT the shipment's BuyerId.
        var result = await _sut.HandleAsync(
            new GetShipmentByOrderQuery(shipment.OrderId, attackerBuyerId), CancellationToken.None);

        // ASSERT — Null, indistinguishable from "shipment not found". A failure here is a
        // CWE-639 IDOR — every "scoped entity by ID" endpoint must have this test (see
        // CLAUDE.md "Testing").
        result.Should().BeNull();
    }
}
