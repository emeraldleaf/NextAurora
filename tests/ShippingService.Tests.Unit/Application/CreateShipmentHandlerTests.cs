using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Application.Handlers;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Tests.Unit.Application;

public class CreateShipmentHandlerTests
{
    private readonly IShipmentRepository _repository = Substitute.For<IShipmentRepository>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly CreateShipmentHandler _sut;

    public CreateShipmentHandlerTests()
    {
        _sut = new CreateShipmentHandler(_repository, _eventPublisher);
    }

    [Fact]
    public async Task Handle_CreatesShipmentAndDispatches()
    {
        // Arrange
        var command = new CreateShipmentCommand(Guid.NewGuid());

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(
            Arg.Is<Shipment>(s => s.Status == ShipmentStatus.Dispatched),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PublishesShipmentDispatchedEvent()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var command = new CreateShipmentCommand(orderId);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<ShipmentDispatchedEvent>(e => e.OrderId == orderId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventContainsTrackingNumber()
    {
        // Arrange
        var command = new CreateShipmentCommand(Guid.NewGuid());

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<ShipmentDispatchedEvent>(e => e.TrackingNumber.StartsWith("NVC-", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenShipmentExistsForOrder_ReturnsExistingShipmentId()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var command = new CreateShipmentCommand(orderId);
        var existingShipment = Shipment.Create(orderId, "FedEx");
        _repository.GetByOrderIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(existingShipment);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().Be(existingShipment.Id);
        await _repository.DidNotReceive().AddAsync(Arg.Any<Shipment>(), Arg.Any<CancellationToken>());
        await _eventPublisher.DidNotReceive().PublishAsync(Arg.Any<ShipmentDispatchedEvent>(), Arg.Any<CancellationToken>());
    }
}
