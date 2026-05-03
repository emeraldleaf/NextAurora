using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// On <see cref="OrderPlacedEvent"/>, build a "your order has been received" notification.
///
/// <para>
/// <b>Wolverine cascading messages:</b> this handler returns a
/// <see cref="SendNotificationRequest"/> command rather than calling <c>IMessageBus</c> directly.
/// Wolverine sees the return type and dispatches the command to <c>SendNotificationHandler</c>
/// next. Same pattern used in <c>OrderPlacedHandler</c> in PaymentService and
/// <c>PaymentCompletedHandler</c> in ShippingService.
/// </para>
/// <para>
/// <b>Why split event-handling from sending:</b> the same <c>SendNotificationRequest</c> is also
/// produced by other notification handlers (<c>PaymentFailedNotificationHandler</c>,
/// <c>ShipmentDispatchedNotificationHandler</c>) and could be invoked from a CLI or future
/// admin endpoint. One sender, many triggers.
/// </para>
/// <para>
/// <b>Returning null on missing recipient:</b> if we can't resolve a recipient for this buyer
/// (test data, race with user-deletion), there's no one to email — silently skip rather than
/// fail the message. Wolverine treats a null return as "no message to dispatch".
/// </para>
/// </summary>
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
