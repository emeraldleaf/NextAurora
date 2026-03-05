using FluentAssertions;
using MediatR;
using NSubstitute;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Application.EventHandlers;

namespace ShippingService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly PaymentCompletedHandler _sut;

    public PaymentCompletedHandlerTests()
    {
        _sut = new PaymentCompletedHandler(_mediator);
    }

    [Fact]
    public async Task Handle_SendsCreateShipmentCommand()
    {
        var notification = new PaymentCompletedNotification(new PaymentCompletedEvent
        {
            OrderId = Guid.NewGuid(),
            PaymentId = Guid.NewGuid(),
            Amount = 100m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Any<CreateShipmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CommandContainsCorrectOrderId()
    {
        var orderId = Guid.NewGuid();
        var notification = new PaymentCompletedNotification(new PaymentCompletedEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            Amount = 100m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<CreateShipmentCommand>(c => c.OrderId == orderId),
            Arg.Any<CancellationToken>());
    }
}
