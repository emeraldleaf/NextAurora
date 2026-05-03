using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

/// <summary>
/// Reacts to <see cref="PaymentFailedEvent"/> — transitions the order to <c>PaymentFailed</c>
/// (terminal state). Mirrors <see cref="PaymentCompletedHandler"/> in structure: existence
/// check, status guard at handler level, status guard at domain level. See that handler's
/// summary for the full idempotency rationale.
///
/// <para>
/// <b>Why no compensation logic here:</b> if payment failed, there's nothing to roll back on
/// the order side — it just stays in PaymentFailed. The buyer places a new order if they want
/// to try again. If we ever introduce stock reservation reversal, that would belong on the
/// PaymentService side (where it can read the order's lines from the event payload) rather
/// than here, because it touches Catalog, not Order.
/// </para>
/// </summary>
public class PaymentFailedHandler(IOrderRepository repository)
{
    public async Task Handle(PaymentFailedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        // Idempotency guard — if the order is no longer in Placed status, a previous delivery
        // of this event already processed it (or PaymentCompletedHandler somehow ran first).
        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaymentFailed();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
