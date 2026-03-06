using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

/// <summary>
/// Handles the PaymentFailedEvent published by PaymentService when a payment gateway
/// call fails (insufficient funds, expired card, gateway timeout, etc.).
///
/// The handler is idempotent: if the order has already moved out of the "Placed" state
/// (e.g. a duplicate event from DLQ replay), the status check short-circuits — no double-update occurs.
///
/// Domain rule: only a Placed order can be transitioned to PaymentFailed.
/// The domain entity enforces this invariant in <see cref="Order.MarkAsPaymentFailed"/>.
/// </summary>
public class PaymentFailedHandler(IOrderRepository repository)
{
    public async Task Handle(PaymentFailedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        // Idempotency guard — if the order is no longer in Placed status, a previous
        // delivery of this event already processed it; skip silently.
        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaymentFailed();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
