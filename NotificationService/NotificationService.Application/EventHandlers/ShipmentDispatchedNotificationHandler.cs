using MediatR;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

public class ShipmentDispatchedNotificationHandler(IMediator mediator, IRecipientResolver recipientResolver)
    : INotificationHandler<ShipmentDispatchedNotification>
{
    public async Task Handle(ShipmentDispatchedNotification notification, CancellationToken cancellationToken)
    {
        var recipient = await recipientResolver.ResolveByOrderIdAsync(notification.Event.OrderId, cancellationToken);
        if (recipient is null) return;

        await mediator.Send(new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Order Shipped",
            $"Your order has been shipped via {notification.Event.Carrier}. Tracking: {notification.Event.TrackingNumber}",
            "Email"), cancellationToken);
    }
}

public record ShipmentDispatchedNotification(ShipmentDispatchedEvent Event) : INotification;
