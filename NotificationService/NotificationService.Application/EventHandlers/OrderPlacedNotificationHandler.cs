using MediatR;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;

namespace NotificationService.Application.EventHandlers;

public class OrderPlacedNotificationHandler(IMediator mediator) : INotificationHandler<OrderPlacedNotification>
{
    public async Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
    {
        await mediator.Send(new SendNotificationRequest(
            notification.Event.BuyerId,
            "", // Email would be resolved from Identity service in production
            "Order Received",
            $"Your order {notification.Event.OrderId} has been received. Total: {notification.Event.TotalAmount:C}",
            "Email"), cancellationToken);
    }
}

public record OrderPlacedNotification(OrderPlacedEvent Event) : INotification;
