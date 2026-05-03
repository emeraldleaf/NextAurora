using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

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
///   <item><b>Existence check</b> — if the order doesn't exist, silently return. Could happen if
///         we receive an event for an order that was deleted (test data scenarios).</item>
///   <item><b>Status guard at the application layer</b> — only transition if the order is still
///         <c>Placed</c>. If it's already <c>Paid</c> from a prior delivery of this same event,
///         we no-op. This avoids the domain method's exception path entirely on the duplicate.</item>
///   <item><b>Status guard inside the domain method</b> (<see cref="Order.MarkAsPaid"/>) — even
///         if step 2 were missed, the entity itself refuses an illegal transition. Defense in
///         depth: the domain layer never trusts callers to be correct.</item>
/// </list>
/// <para>
/// <b>Concurrency safety:</b> the order's <c>RowVersion</c> token guards the read-modify-save.
/// If <see cref="ShipmentDispatchedHandler"/> mutates the same order between this handler's
/// load and save, we get <c>DbUpdateConcurrencyException</c> on save; Wolverine's
/// <c>AddConcurrencyRetry</c> policy retries with backoff (the retry will refetch and the
/// status guard will likely cause it to no-op the second time around — exactly the behavior
/// we want).
/// </para>
/// </summary>
public class PaymentCompletedHandler(IOrderRepository repository)
{
    public async Task Handle(PaymentCompletedEvent @event, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(@event.OrderId, cancellationToken);
        if (order is null) return;

        // Idempotency check — see class summary.
        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaid();
        await repository.UpdateAsync(order, cancellationToken);
    }
}
