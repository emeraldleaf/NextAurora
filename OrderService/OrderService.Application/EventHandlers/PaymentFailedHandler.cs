using MediatR;
using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

/// <summary>
/// Handles the PaymentFailedEvent published by PaymentService when a payment gateway
/// call fails (insufficient funds, expired card, gateway timeout, etc.).
///
/// The handler is idempotent: if the order has already moved out of the "Placed" state
/// (e.g. a duplicate event from DLQ replay), <see cref="IOrderRepository.GetByIdAsync"/>
/// returns null (deleted) or the status check short-circuits — no double-update occurs.
///
/// Domain rule: only a Placed order can be transitioned to PaymentFailed.
/// The domain entity enforces this invariant in <see cref="Order.MarkAsPaymentFailed"/>.
/// </summary>
public class PaymentFailedHandler(IOrderRepository repository) : INotificationHandler<PaymentFailedNotification>
{
    public async Task Handle(PaymentFailedNotification notification, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(notification.Event.OrderId, cancellationToken);
        if (order is null) return;

        // Idempotency guard — if the order is no longer in Placed status, a previous
        // delivery of this event already processed it; skip silently.
        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaymentFailed();
        await repository.UpdateAsync(order, cancellationToken);
    }
}

/// <summary>
/// MediatR notification wrapper that carries a <see cref="PaymentFailedEvent"/> through
/// the pipeline.  Using a wrapper record keeps the Application layer decoupled from the
/// Infrastructure-level deserialization details.
/// </summary>
public record PaymentFailedNotification(PaymentFailedEvent Event) : INotification;
