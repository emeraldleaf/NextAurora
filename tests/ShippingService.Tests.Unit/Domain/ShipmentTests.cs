using FluentAssertions;
using ShippingService.Domain.Entities;

namespace ShippingService.Tests.Unit.Domain;

public class ShipmentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsCreatedShipment()
    {
        // Arrange & Act
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        // Assert
        shipment.Status.Should().Be(ShipmentStatus.Created);
        shipment.Id.Should().NotBeEmpty();
        shipment.Carrier.Should().Be("FedEx");
    }

    [Fact]
    public void Create_GeneratesTrackingNumberWithNvcPrefix()
    {
        // Arrange & Act
        var shipment = Shipment.Create(Guid.NewGuid(), "UPS");

        // Assert
        shipment.TrackingNumber.Should().StartWith("NVC-");
        shipment.TrackingNumber.Should().HaveLength(16); // "NVC-" + 12 hex chars
    }

    [Fact]
    public void Dispatch_SetsStatusToDispatched()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        // Act
        shipment.Dispatch();

        // Assert
        shipment.Status.Should().Be(ShipmentStatus.Dispatched);
        shipment.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_AddsTrackingEvent()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        // Act
        shipment.Dispatch();

        // Assert
        shipment.TrackingEvents.Should().ContainSingle();
        shipment.TrackingEvents[0].Description.Should().Be("Package dispatched");
    }

    [Fact]
    public void MarkDelivered_SetsDeliveredAt()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");
        shipment.Dispatch();
        shipment.MarkInTransit();

        // Act
        shipment.MarkDelivered();

        // Assert
        shipment.Status.Should().Be(ShipmentStatus.Delivered);
        shipment.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkInTransit_FromCreated_ShouldRequireDispatched()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        // Act
        var act = () => shipment.MarkInTransit();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_FromCreated_ShouldRequireDispatched()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");

        // Act
        var act = () => shipment.MarkDelivered();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_FromDispatched_ShouldRequireInTransit()
    {
        // Arrange
        var shipment = Shipment.Create(Guid.NewGuid(), "FedEx");
        shipment.Dispatch();

        // Act
        var act = () => shipment.MarkDelivered();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
