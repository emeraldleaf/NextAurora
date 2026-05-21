using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// Three event handlers in one class. Each <c>Handle</c> overload converts a domain event from
/// another service into a <see cref="SendNotificationRequest"/>; Wolverine sees the returned
/// command and cascades it to <see cref="SendNotificationHandler"/>.
///
/// <para>
/// <b>Why merged:</b> each handler is pure event-to-command mapping with no state and no
/// branching beyond string formatting. Splitting them into separate classes was uniform with
/// the other services but didn't earn its keep here. If one of these grows real logic
/// (lookup against a user-prefs cache, channel selection, A/B copy), promote it back into its
/// own file at that point.
/// </para>
/// <para>
/// <b>Placeholder recipient lookup:</b> the emails below are deterministic fakes derived from
/// the event's IDs. There used to be an <c>IRecipientResolver</c> abstraction with a stub
/// implementation that returned the same shape — deleted because the stub didn't enforce any
/// contract that mattered. When a real recipient lookup lands (gRPC to a user service, local
/// cache hydrated from <c>UserCreated</c> events), introduce the seam then: handlers gain a
/// constructor dependency and return <c>null</c> when the buyer isn't found (Wolverine treats
/// null as "no message to dispatch").
/// </para>
/// </summary>
public static class NotificationEventHandlers
{
    public static SendNotificationRequest Handle(OrderPlacedEvent @event)
    {
        var email = $"buyer-{@event.BuyerId:N}@placeholder.local";
        return new SendNotificationRequest(
            @event.BuyerId,
            email,
            "Order Received",
            $"Your order {@event.OrderId} has been received. Total: {@event.TotalAmount:C}",
            "Email");
    }

    /// <summary>
    /// <b>Note on user-facing copy:</b> we reflect <see cref="PaymentFailedEvent.Reason"/> into
    /// the email body. The Reason field comes from the payment gateway and is intended for
    /// internal audit; in production this should be filtered/translated to a user-friendly
    /// message rather than passing the raw provider error code through. Tracked as future
    /// product-approved copy work.
    /// </summary>
    public static SendNotificationRequest Handle(PaymentFailedEvent @event)
    {
        var email = $"buyer-{@event.BuyerId:N}@placeholder.local";
        var body = $"Your payment for order {@event.OrderId} could not be processed. " +
                   $"Reason: {@event.Reason}. Please update your payment method and try again.";

        return new SendNotificationRequest(
            @event.BuyerId,
            email,
            "Payment Failed",
            body,
            "Email");
    }

    /// <summary>
    /// <see cref="ShipmentDispatchedEvent"/> doesn't carry a buyer ID — when a real recipient
    /// lookup lands, this would resolve order → buyer → email via the user service.
    /// </summary>
    public static SendNotificationRequest Handle(ShipmentDispatchedEvent @event)
    {
        var email = $"order-{@event.OrderId:N}@placeholder.local";
        return new SendNotificationRequest(
            @event.OrderId,
            email,
            "Order Shipped",
            $"Your order has been shipped via {@event.Carrier}. Tracking: {@event.TrackingNumber}",
            "Email");
    }
}
