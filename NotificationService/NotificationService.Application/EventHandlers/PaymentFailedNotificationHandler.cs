using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// On <see cref="PaymentFailedEvent"/>, build a "payment failed, try again" notification. Same
/// cascading-messages pattern as <see cref="OrderPlacedNotificationHandler"/>.
///
/// <para>
/// <b>Note on user-facing copy:</b> we reflect <see cref="PaymentFailedEvent.Reason"/> into the
/// email body. The Reason field comes from the payment gateway and is intended for internal
/// audit; in production this should be filtered/translated to a user-friendly message rather
/// than passing the raw provider error code through. Tracked as future product-approved copy work.
/// </para>
/// </summary>
public class PaymentFailedNotificationHandler(IRecipientResolver recipientResolver)
{
    public async Task<SendNotificationRequest?> Handle(PaymentFailedEvent @event, CancellationToken cancellationToken)
    {
        var recipient = await recipientResolver.ResolveByBuyerIdAsync(@event.BuyerId, cancellationToken);
        if (recipient is null) return null;

        var body = $"Your payment for order {@event.OrderId} could not be processed. " +
                   $"Reason: {@event.Reason}. Please update your payment method and try again.";

        return new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Payment Failed",
            body,
            "Email");
    }
}
