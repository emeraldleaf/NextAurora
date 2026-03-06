using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using OrderService.Application.EventHandlers;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class ShipmentDispatchedHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly ShipmentDispatchedHandler _sut;

    public ShipmentDispatchedHandlerTests()
    {
        _sut = new ShipmentDispatchedHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenOrderExists_MarksOrderAsShipped()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            OrderId = order.Id,
            ShipmentId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-123",
            DispatchedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        order.Status.Should().Be(OrderStatus.Shipped);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutError()
    {
        // Arrange
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            OrderId = Guid.NewGuid(),
            ShipmentId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-123",
            DispatchedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        // Act
        var act = () => _sut.Handle(notification, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_WhenOrderNotPaid_IsIdempotent()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            OrderId = order.Id,
            ShipmentId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-123",
            DispatchedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // Act
        var act = () => _sut.Handle(notification, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
