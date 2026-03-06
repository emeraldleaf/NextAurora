using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;

namespace ShippingService.Application.EventHandlers;

/// <summary>
/// Handles the PaymentCompletedEvent by cascading a CreateShipmentCommand.
/// Wolverine automatically dispatches the returned command to CreateShipmentHandler.
/// </summary>
public static class PaymentCompletedHandler
{
    public static CreateShipmentCommand Handle(PaymentCompletedEvent @event)
        => new(@event.OrderId);
}
