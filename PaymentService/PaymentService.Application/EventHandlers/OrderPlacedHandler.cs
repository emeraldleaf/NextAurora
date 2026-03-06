using NextAurora.Contracts.Events;
using PaymentService.Application.Commands;

namespace PaymentService.Application.EventHandlers;

/// <summary>
/// Handles the OrderPlacedEvent by cascading a ProcessPaymentCommand.
/// Wolverine automatically dispatches the returned command to ProcessPaymentHandler.
/// </summary>
public static class OrderPlacedHandler
{
    public static ProcessPaymentCommand Handle(OrderPlacedEvent @event)
        => new(@event.OrderId, @event.TotalAmount, @event.Currency, @event.BuyerId);
}
