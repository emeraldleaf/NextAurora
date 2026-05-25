using AwesomeAssertions;
using NextAurora.Contracts.DTOs;
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

    private static ShipmentDto SampleDto(Guid orderId, Guid buyerId, string carrier = "FedEx") => new(
        Id: Guid.NewGuid(),
        OrderId: orderId,
        BuyerId: buyerId,
        Carrier: carrier,
        TrackingNumber: "NVC-12345",
        Status: nameof(ShipmentStatus.Dispatched),
        CreatedAt: DateTime.UtcNow,
        DispatchedAt: DateTime.UtcNow,
        TrackingEvents: [new TrackingEventDto("Dispatched", "Dispatched", DateTime.UtcNow)]);

    [Fact]
    public async Task Handle_WhenOwnerRequests_ReturnsShipmentDtoFromReadProjection()
    {
        // ARRANGE — The read path goes through GetSummaryByOrderIdAsync, which projects to
        // ShipmentDto in EF (AsNoTracking + Select). No entity ever materializes. The
        // ownership check happens on the DTO's BuyerId field — see docs/cqrs-data-access.md.
        var buyerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var dto = SampleDto(orderId, buyerId);
        _repository.GetSummaryByOrderIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(dto);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(
            new GetShipmentByOrderQuery(orderId, buyerId), CancellationToken.None);

        // ASSERT — Four invariants:
        //  1) Non-null — the shipment was found and the caller owns it.
        //  2) DTO passes through unchanged (handler is a near-passthrough after the
        //     ownership check; no mapping happens here — the projection already produced
        //     the final shape).
        //  3) The entity-returning GetByOrderIdAsync stays untouched on the read path.
        //     That method is now the write loader for CreateShipmentHandler's idempotency
        //     check; calling it here would reintroduce entity materialization on a read.
        //  4) Status is the STRING form (the endpoint MUST NOT expose the enum integer).
        //     The projection in the repository is the enforcement point.
        result.Should().NotBeNull();
        result.Should().BeSameAs(dto);
        await _repository.DidNotReceive().GetByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        result!.Status.Should().Be(nameof(ShipmentStatus.Dispatched));
    }

    [Fact]
    public async Task Handle_WhenShipmentNotFound_ReturnsNull()
    {
        // ARRANGE — The order may not have reached the shipping stage yet (payment still
        // pending) or never will (payment failed). Null is the unambiguous "no shipment".
        _repository.GetSummaryByOrderIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
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
        //
        // Importantly, with projection-in-EF the ownership check happens on the DTO's
        // BuyerId field rather than an entity instance — same security boundary, no
        // entity hop. The DTO carries BuyerId specifically so the handler can enforce
        // this check without materializing the aggregate.
        var ownerBuyerId = Guid.NewGuid();
        var attackerBuyerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _repository.GetSummaryByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(SampleDto(orderId, ownerBuyerId));

        // ACT — attackerBuyerId is NOT the shipment's BuyerId.
        var result = await _sut.HandleAsync(
            new GetShipmentByOrderQuery(orderId, attackerBuyerId), CancellationToken.None);

        // ASSERT — Null, indistinguishable from "shipment not found". A failure here is a
        // CWE-639 IDOR — every "scoped entity by ID" endpoint must have this test (see
        // CLAUDE.md "Testing").
        result.Should().BeNull();
    }
}
