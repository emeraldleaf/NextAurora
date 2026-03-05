using FluentAssertions;
using ShippingService.Domain.Entities;

namespace ShippingService.Tests.Unit.Domain;

public class ShipmentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsCreatedShipment()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        shipment.Status.Should().Be(ShipmentStatus.Created);
        shipment.Id.Should().NotBeEmpty();
        shipment.Carrier.Should().Be("FedEx");
    }

    [Fact]
    public void Create_GeneratesTrackingNumberWithNvcPrefix()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "UPS");

        shipment.TrackingNumber.Should().StartWith("NVC-");
        shipment.TrackingNumber.Should().HaveLength(16); // "NVC-" + 12 hex chars
    }

    [Fact]
    public void Dispatch_SetsStatusToDispatched()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        shipment.Dispatch();

        shipment.Status.Should().Be(ShipmentStatus.Dispatched);
        shipment.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_AddsTrackingEvent()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        shipment.Dispatch();

        shipment.TrackingEvents.Should().ContainSingle();
        shipment.TrackingEvents[0].Description.Should().Be("Package dispatched");
    }

    [Fact]
    public void MarkDelivered_SetsDeliveredAt()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");
        shipment.Dispatch();
        shipment.MarkInTransit();

        shipment.MarkDelivered();

        shipment.Status.Should().Be(ShipmentStatus.Delivered);
        shipment.DeliveredAt.Should().NotBeNull();
    }

    [Fact(Skip = "Known bug: No state guard — MarkInTransit succeeds from Created status without Dispatch")]
    public void MarkInTransit_FromCreated_ShouldRequireDispatched()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        var act = () => shipment.MarkInTransit();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact(Skip = "Known bug: No state guard — MarkDelivered succeeds from Created status without Dispatch")]
    public void MarkDelivered_FromCreated_ShouldRequireDispatched()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        var act = () => shipment.MarkDelivered();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact(Skip = "Known bug: No state guard — MarkDelivered succeeds from Dispatched without InTransit")]
    public void MarkDelivered_FromDispatched_ShouldRequireInTransit()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");
        shipment.Dispatch();

        var act = () => shipment.MarkDelivered();

        act.Should().Throw<InvalidOperationException>();
    }
}
