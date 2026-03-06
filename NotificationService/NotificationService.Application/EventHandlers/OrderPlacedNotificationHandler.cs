using MediatR;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.Interfaces;

namespace NotificationService.Application.EventHandlers;

public class OrderPlacedNotificationHandler(IMediator mediator, IRecipientResolver recipientResolver)
    : INotificationHandler<OrderPlacedNotification>
{
    public async Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        var recipient = await recipientResolver.ResolveByBuyerIdAsync(notification.Event.BuyerId, cancellationToken);
        if (recipient is null) return;

        await mediator.Send(new SendNotificationRequest(
            recipient.BuyerId,
            recipient.Email,
            "Order Received",
            $"Your order {notification.Event.OrderId} has been received. Total: {notification.Event.TotalAmount:C}",
            "Email"), cancellationToken);
    }
}

public record OrderPlacedNotification(OrderPlacedEvent Event) : INotification;
