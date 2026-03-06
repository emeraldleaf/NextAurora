using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// Handles the PaymentFailedEvent published by PaymentService when a payment gateway
/// call fails.  Returns a SendNotificationRequest which Wolverine automatically cascades
/// to SendNotificationHandler.
///
/// If the buyer cannot be resolved the handler returns null — Wolverine ignores null returns,
/// so no notification is sent without infinite retries.
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
