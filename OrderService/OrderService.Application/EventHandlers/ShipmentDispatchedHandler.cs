using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

/// <summary>
/// Reacts to <see cref="ShipmentDispatchedEvent"/> — transitions <c>Paid → Shipped</c>. Same
/// idempotency pattern as <see cref="PaymentCompletedHandler"/>: load, status-guard, mutate,
/// save. Status guard checks <c>Paid</c> here because shipping only follows successful payment.
///
/// <para>
/// <b>Saga ordering:</b> we trust the natural ordering: payment must succeed before shipping
/// can be created (by <c>CreateShipmentHandler</c> in ShippingService, which itself reacts to
/// <see cref="PaymentCompletedEvent"/>). If the events somehow arrive out of order at this
/// handler — extremely unlikely with single-subscription Service Bus, but possible after a
/// DLQ replay — the status guard catches it: an order still in <c>Placed</c> can't go to
/// <c>Shipped</c>, so we silently skip.
/// </para>
/// </summary>
public class ShipmentDispatchedHandler(IOrderRepository repository)
{
    public async Task Handle(ShipmentDispatchedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        // Idempotency guard — already shipped or in some other state means a duplicate delivery.
        if (order.Status != OrderStatus.Paid) return;

        order.MarkAsShipped();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
