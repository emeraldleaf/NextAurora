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
        var order = OrderBuilder.Default().Build();
        var @event = new PaymentCompletedEvent
        {
            OrderId = order.Id,
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        await _sut.Handle(@event, CancellationToken.None);

        order.Status.Should().Be(OrderStatus.Paid);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutError()
    {
        var @event = new PaymentCompletedEvent
        {
            OrderId = Guid.NewGuid(),
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var act = () => _sut.Handle(@event, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderAlreadyPaid_IsIdempotent()
    {
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        var @event = new PaymentCompletedEvent
        {
            OrderId = order.Id,
            PaymentId = Guid.NewGuid(),
            Amount = 10m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        var act = () => _sut.Handle(@event, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}

