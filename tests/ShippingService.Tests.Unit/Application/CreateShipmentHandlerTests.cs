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
        var command = new CreateShipmentCommand(Guid.NewGuid());

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(
            Arg.Is<Shipment>(s => s.Status == ShipmentStatus.Dispatched),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PublishesShipmentDispatchedEvent()
    {
        var orderId = Guid.NewGuid();
        var command = new CreateShipmentCommand(orderId);

        await _sut.Handle(command, CancellationToken.None);

        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<ShipmentDispatchedEvent>(e => e.OrderId == orderId),
            "shipping-events",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventContainsTrackingNumber()
    {
        var command = new CreateShipmentCommand(Guid.NewGuid());

        await _sut.Handle(command, CancellationToken.None);

        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<ShipmentDispatchedEvent>(e => e.TrackingNumber.StartsWith("NVC-", StringComparison.Ordinal)),
            "shipping-events",
            Arg.Any<CancellationToken>());
    }
}
