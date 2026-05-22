using AwesomeAssertions;
using ShippingService.Domain;

namespace ShippingService.Tests.Unit.Domain;

public class ShipmentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsCreatedShipment()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        shipment.Status.Should().Be(ShipmentStatus.Created);
        shipment.Id.Should().NotBeEmpty();
        shipment.Carrier.Should().Be("FedEx");
    }

    [Fact]
    public void Create_GeneratesTrackingNumberWithNvcPrefix()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "UPS");

        shipment.TrackingNumber.Should().StartWith("NVC-");
        shipment.TrackingNumber.Should().HaveLength(16); // "NVC-" + 12 hex chars
    }

    [Fact]
    public void Dispatch_SetsStatusToDispatched()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        shipment.Dispatch();

        shipment.Status.Should().Be(ShipmentStatus.Dispatched);
        shipment.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_AddsTrackingEvent()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");

        shipment.Dispatch();

        shipment.TrackingEvents.Should().ContainSingle();
        shipment.TrackingEvents[0].Description.Should().Be("Package dispatched");
    }

    [Fact]
    public void Dispatch_FromAlreadyDispatched_ShouldThrow()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), "FedEx");
        shipment.Dispatch();

        var act = () => shipment.Dispatch();

        act.Should().Throw<InvalidOperationException>();
    }
}
