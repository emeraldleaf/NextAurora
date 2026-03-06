using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

public class OrderPlacedNotificationHandler(IRecipientResolver recipientResolver)
{
    public async Task<SendNotificationRequest?> Handle(OrderPlacedEvent @event, CancellationToken cancellationToken)
    {
        var recipient = await recipientResolver.ResolveByBuyerIdAsync(@event.BuyerId, cancellationToken);
        if (recipient is null) return null;

        return new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Order Received",
            $"Your order {@event.OrderId} has been received. Total: {@event.TotalAmount:C}",
            "Email");
    }
}
