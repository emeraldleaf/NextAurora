using MediatR;
using NextAurora.Contracts.Events;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.EventHandlers;

public class PaymentCompletedHandler(IOrderRepository repository) : INotificationHandler<PaymentCompletedNotification>
{
    public async Task Handle(PaymentCompletedNotification notification, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(notification.Event.OrderId, cancellationToken);
        if (order is null) return;

        if (order.Status != OrderStatus.Placed) return;

        order.MarkAsPaid();
        await repository.UpdateAsync(order, cancellationToken);
    }
}

public record PaymentCompletedNotification(PaymentCompletedEvent Event) : INotification;
