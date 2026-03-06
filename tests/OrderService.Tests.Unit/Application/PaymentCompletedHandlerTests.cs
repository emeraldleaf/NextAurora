using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using OrderService.Application.EventHandlers;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly PaymentCompletedHandler _sut;

    public PaymentCompletedHandlerTests()
    {
        _sut = new PaymentCompletedHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenOrderExists_MarksOrderAsPaid()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        var notification = new PaymentCompletedNotification(new PaymentCompletedEvent
        {
            OrderId = order.Id,
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        order.Status.Should().Be(OrderStatus.Paid);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutError()
    {
        // Arrange
        var notification = new PaymentCompletedNotification(new PaymentCompletedEvent
        {
            OrderId = Guid.NewGuid(),
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        // Act
        var act = () => _sut.Handle(notification, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderAlreadyPaid_IsIdempotent()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        var notification = new PaymentCompletedNotification(new PaymentCompletedEvent
        {
            OrderId = order.Id,
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        });
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // Act
        var act = () => _sut.Handle(notification, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
