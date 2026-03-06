using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

public class ShipmentDispatchedHandler(IOrderRepository repository)
{
    public async Task Handle(ShipmentDispatchedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        if (order.Status != OrderStatus.Paid) return;

        order.MarkAsShipped();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
