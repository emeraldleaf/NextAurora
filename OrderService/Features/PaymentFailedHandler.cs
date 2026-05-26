using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.Events;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Features;

/// <summary>
/// Reacts to <see cref="PaymentFailedEvent"/> — transitions the order to <c>PaymentFailed</c>
/// (terminal state). Mirrors <see cref="PaymentCompletedHandler"/> in structure: existence
/// check, status guard at handler level, status guard at domain level.
///
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
