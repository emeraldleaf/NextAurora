using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

public class ShipmentDispatchedNotificationHandler(IRecipientResolver recipientResolver)
{
    public async Task<SendNotificationRequest?> Handle(ShipmentDispatchedEvent @event, CancellationToken cancellationToken)
    {
        var recipient = await recipientResolver.ResolveByOrderIdAsync(@event.OrderId, cancellationToken);
        if (recipient is null) return null;

        return new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Order Shipped",
            $"Your order has been shipped via {@event.Carrier}. Tracking: {@event.TrackingNumber}",
            "Email");
    }
}
