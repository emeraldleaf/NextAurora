using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// On <see cref="ShipmentDispatchedEvent"/>, build a "your order has shipped" notification with
/// tracking info. Same cascading-messages pattern as the other notification handlers.
///
/// <para>
/// <b>Different recipient lookup:</b> here we resolve by <c>OrderId</c> rather than
/// <c>BuyerId</c>, because <see cref="ShipmentDispatchedEvent"/> doesn't carry a buyer ID.
/// In a real system the resolver would look up the order's buyer; today
/// <c>StubRecipientResolver</c> returns a placeholder either way.
/// </para>
/// </summary>
public class ShipmentDispatchedNotificationHandler(IRecipientResolver recipientResolver)
{
    public async Task<SendNotificationRequest?> HandleAsync(ShipmentDispatchedEvent @event, CancellationToken cancellationToken)
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
