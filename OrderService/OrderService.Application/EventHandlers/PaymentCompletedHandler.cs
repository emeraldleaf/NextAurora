using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

public class PaymentCompletedHandler(IOrderRepository repository)
{
    public async Task Handle(PaymentCompletedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaid();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
