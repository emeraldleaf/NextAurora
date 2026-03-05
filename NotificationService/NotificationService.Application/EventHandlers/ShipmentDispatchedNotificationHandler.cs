using MediatR;
using NovaCraft.Contracts.Events;
using NotificationService.Application.Commands;

namespace NotificationService.Application.EventHandlers;

public class ShipmentDispatchedNotificationHandler(IMediator mediator) : INotificationHandler<ShipmentDispatchedNotification>
{
    public async Task Handle(ShipmentDispatchedNotification notification, CancellationToken cancellationToken)
    {
        await mediator.Send(new SendNotificationRequest(
            Guid.Empty, // Buyer ID would be resolved via Order lookup in production
            "",
            "Order Shipped",
            $"Your order has been shipped via {notification.Event.Carrier}. Tracking: {notification.Event.TrackingNumber}",
            "Email"), cancellationToken);
    }
}

public record ShipmentDispatchedNotification(ShipmentDispatchedEvent Event) : INotification;
