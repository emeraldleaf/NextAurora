using MediatR;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;

namespace ShippingService.Application.EventHandlers;

public class PaymentCompletedHandler(IMediator mediator) : INotificationHandler<PaymentCompletedNotification>
{
    public async Task Handle(PaymentCompletedNotification notification, CancellationToken cancellationToken)
    {
        await mediator.Send(new CreateShipmentCommand(notification.Event.OrderId), cancellationToken);
    }
}

public record PaymentCompletedNotification(PaymentCompletedEvent Event) : INotification;
