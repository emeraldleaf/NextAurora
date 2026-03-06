using MediatR;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

/// <summary>
/// Handles the PaymentFailedEvent published by PaymentService when a payment gateway
/// call fails.  Sends the buyer an email (or other configured channel) explaining that
/// their payment could not be processed and they should retry.
///
/// IRecipientResolver looks up the buyer's contact details (email, push token, etc.)
/// from an external identity or user-profile service.  If the buyer cannot be resolved
/// (e.g. test data, deleted account) the handler returns silently — no notification is
/// sent and the event is still completed (not abandoned) to avoid infinite retries.
///
/// The SendNotificationRequest dispatches to SendNotificationHandler which selects the
/// right delivery channel (Email, Push, SMS) and records the notification in the database.
/// </summary>
public class PaymentFailedNotificationHandler(IMediator mediator, IRecipientResolver recipientResolver)
    : INotificationHandler<PaymentFailedNotification>
{
    public async Task Handle(PaymentFailedNotification notification, CancellationToken cancellationToken)
    {
        // Resolve contact details for the buyer using the BuyerId from the event.
        // PaymentService includes BuyerId in PaymentFailedEvent so NotificationService
        // does not need to call back to OrderService to find out who placed the order.
        var recipient = await recipientResolver.ResolveByBuyerIdAsync(notification.Event.BuyerId, cancellationToken);
        if (recipient is null) return;

        // Build a concise, actionable message.  The Reason field comes directly from the
        // payment gateway (e.g. "insufficient_funds") and is displayed to the user.
        var body = $"Your payment for order {notification.Event.OrderId} could not be processed. " +
                   $"Reason: {notification.Event.Reason}. Please update your payment method and try again.";

        await mediator.Send(new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Payment Failed",
            body,
            "Email"), cancellationToken);
    }
}

/// <summary>
/// MediatR notification wrapper carrying a <see cref="PaymentFailedEvent"/> for dispatch
/// within NotificationService's pipeline.
/// </summary>
public record PaymentFailedNotification(PaymentFailedEvent Event) : INotification;
