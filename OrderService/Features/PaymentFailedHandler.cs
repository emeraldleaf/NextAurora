using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.Events;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Features;

/// <summary>
/// Reacts to <see cref="PaymentFailedEvent"/> — transitions the order from <c>Placed</c> to
/// <c>PaymentFailed</c> (terminal state). The other half of the payment-side of the saga is
/// <see cref="PaymentCompletedHandler"/>.
///
/// <para>
/// <b>Idempotency layered three ways</b> — important because RabbitMQ delivers at-least-once
/// and a redelivery here is normal, not exceptional:
/// </para>
/// <list type="number">
///   <item><b>Existence check</b> — if the order doesn't exist, silently return.</item>
///   <item><b>Status guard at the handler level</b> — only transition if the order is still
///         <c>Placed</c>. If it's already <c>PaymentFailed</c> from a prior delivery (or
///         already <c>Paid</c>, e.g. competing PaymentCompleted won the race), no-op.</item>
///   <item><b>Status guard inside the domain method</b> (<see cref="Order.MarkAsPaymentFailed"/>)
///         — even if step 2 were missed, the entity itself refuses an illegal transition.</item>
/// </list>
/// <para>
/// <b>Concurrency safety:</b> the order's <c>RowVersion</c> token guards the read-modify-save.
/// If another handler (<see cref="PaymentCompletedHandler"/> for a competing delivery, or
/// <see cref="ShipmentDispatchedHandler"/> if status somehow advanced) mutates the same order
/// between this handler's load and save, we get <c>DbUpdateConcurrencyException</c> on save;
/// Wolverine's <c>AddConcurrencyRetry</c> policy retries with backoff. Wolverine's
/// AutoApplyTransactions wraps the SaveChangesAsync below in the same DB transaction as any
/// outbox envelope staged during this handler (none today; the handler is read-modify-save only).
/// </para>
/// <para>
/// <b>Why no compensation logic here:</b> if payment failed, there's nothing to roll back on
/// the order side — it stays in PaymentFailed. The buyer places a new order if they want to try
/// again. If we ever introduce stock reservation reversal, that belongs on the PaymentService
/// side (where it can read the order's lines from the event payload) rather than here.
/// </para>
/// </summary>
public class PaymentFailedHandler(OrderDbContext context)
{
    public async Task HandleAsync(PaymentFailedEvent @event, CancellationToken cancellationToken)
    {
        var order = await context.Orders.FirstOrDefaultAsync(o => o.Id == @event.OrderId, cancellationToken);
        if (order is null) return;

        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaymentFailed();
        await context.SaveChangesAsync(cancellationToken);
    }
}
