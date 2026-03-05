using MediatR;
using NextAurora.Contracts.Events;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

public class ShipmentDispatchedHandler(IOrderRepository repository) : INotificationHandler<ShipmentDispatchedNotification>
{
    public async Task Handle(ShipmentDispatchedNotification notification, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(notification.Event.OrderId, cancellationToken);
        if (order is null) return;

        order.MarkAsShipped();
        await repository.UpdateAsync(order, cancellationToken);
    }
}

public record ShipmentDispatchedNotification(ShipmentDispatchedEvent Event) : INotification;
