using NextAurora.Contracts.Events;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// Reacts to <see cref="PaymentCompletedEvent"/> in OrderService — transitions the order from
/// <c>Placed → Paid</c>. This is one half of the payment-side of the saga (the other half is
/// <see cref="PaymentFailedHandler"/>).
///
/// <para>
/// <b>Idempotency layered three ways</b> — important because Service Bus delivers at-least-once
/// and a redelivery here is normal, not exceptional:
/// </para>
/// <list type="number">
///   <item><b>Existence check</b> — if the order doesn't exist, silently return.</item>
///   <item><b>Status guard at the handler level</b> — only transition if the order is still
///         <c>Placed</c>. If it's already <c>Paid</c> from a prior delivery, no-op.</item>
///   <item><b>Status guard inside the domain method</b> (<see cref="Order.MarkAsPaid"/>) — even
///         if step 2 were missed, the entity itself refuses an illegal transition.</item>
/// </list>
/// <para>
/// <b>Concurrency safety:</b> the order's <c>RowVersion</c> token guards the read-modify-save.
/// If <see cref="ShipmentDispatchedHandler"/> mutates the same order between this handler's
/// load and save, we get <c>DbUpdateConcurrencyException</c> on save; Wolverine's
/// <c>AddConcurrencyRetry</c> policy retries with backoff.
/// </para>
/// </summary>
public class PaymentCompletedHandler(IOrderRepository repository)
{
    public async Task HandleAsync(PaymentCompletedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaid();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
