using MediatR;
using NovaCraft.Contracts.Events;
using PaymentService.Application.Commands;

namespace PaymentService.Application.EventHandlers;

public class OrderPlacedHandler(IMediator mediator) : INotificationHandler<OrderPlacedNotification>
{
    public async Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        await mediator.Send(new ProcessPaymentCommand(
            notification.Event.OrderId,
            notification.Event.TotalAmount,
            notification.Event.Currency), cancellationToken);
    }
}

public record OrderPlacedNotification(OrderPlacedEvent Event) : INotification;
