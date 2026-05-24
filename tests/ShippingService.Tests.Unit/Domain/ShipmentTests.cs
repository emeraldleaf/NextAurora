using AwesomeAssertions;
using ShippingService.Domain;

namespace ShippingService.Tests.Unit.Domain;

public class ShipmentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsCreatedShipment()
    {
        // ARRANGE — Nothing to set up: Shipment.Create is the factory chokepoint. Like
        // every aggregate in this repo, it validates up front and produces a fully
        // constituted aggregate (no two-phase init).

        // ACT
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        // ASSERT — Three invariants:
        //  1) Status starts as Created — the shipment's start state (Dispatched is the
        //     only live transition today; see Shipment.cs class doc for the dead-branch
        //     story around InTransit/Delivered).
        //  2) Id is server-generated.
        //  3) Carrier carries through unchanged.
        shipment.Status.Should().Be(ShipmentStatus.Created);
        shipment.Id.Should().NotBeEmpty();
        shipment.Carrier.Should().Be("FedEx");
    }

    [Fact]
    public void Create_GeneratesTrackingNumberWithNvcPrefix()
    {
        // ARRANGE — In production, carriers usually return a tracking number after a
        // label-creation API call. We generate locally as a placeholder so the domain
        // stays decoupled from carrier integration code — easy to swap when real
        // carriers wire in. Format: "NVC-" + 12 uppercase hex chars = 16 chars.

        // ACT
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "UPS");

        // ASSERT — Two invariants:
        //  1) NVC- prefix is consistent (lets log parsers / UI distinguish our tracking
        //     IDs from real carrier IDs once integration lands).
        //  2) Length is exactly 16 chars (4-char prefix + 12-char hex tail).
        shipment.TrackingNumber.Should().StartWith("NVC-");
        shipment.TrackingNumber.Should().HaveLength(16);
    }

    [Fact]
    public void Dispatch_SetsStatusToDispatched()
    {
        // ARRANGE — Happy-path saga transition: ShippingService creates the shipment
        // (Created) then immediately dispatches it. Today the create+dispatch happens
        // in CreateShipmentHandler so the live transition Created → Dispatched is the
        // entire shipment lifecycle the user sees.
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        // ACT
        shipment.Dispatch();

        // ASSERT — Two invariants:
        //  1) Status is now Dispatched.
        //  2) DispatchedAt is non-null — surfaces on the order detail page as
        //     "shipped on" — and is the source for the ShipmentDispatchedEvent's timestamp.
        shipment.Status.Should().Be(ShipmentStatus.Dispatched);
        shipment.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_AddsTrackingEvent()
    {
        // ARRANGE — Each state transition writes a TrackingEvent into the aggregate's
        // child collection. This is how the buyer-facing tracking history materializes —
        // not from polling the carrier, but from our own state transitions. (Carrier
        // polling is the not-yet-built feature.)
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        // ACT
        shipment.Dispatch();

        // ASSERT — Two invariants:
        //  1) Exactly one tracking event was added (no duplicates from the transition).
        //  2) The description is the canonical string the UI can rely on.
        shipment.TrackingEvents.Should().ContainSingle();
        shipment.TrackingEvents[0].Description.Should().Be("Package dispatched");
    }

    [Fact]
    public void Dispatch_FromAlreadyDispatched_ShouldThrow()
    {
        // ARRANGE — Status-guard idempotency check. Wolverine is at-least-once: a
        // redelivered "create+dispatch" command must NOT double-dispatch the same
        // shipment. The HANDLER catches the throw and short-circuits; the DOMAIN-level
        // guard exists so that even a bypassed handler can't corrupt state.
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");
        shipment.Dispatch();

        // ACT
        var act = () => shipment.Dispatch();

        // ASSERT — The throw is the contract. If a future refactor removes the
        // guard, the handler's idempotency story falls back to "best effort" — this
        // test fails immediately and catches the regression.
        act.Should().Throw<InvalidOperationException>();
    }
}
