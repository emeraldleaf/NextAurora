using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;

namespace ShippingService.Application.EventHandlers;

/// <summary>
/// ShippingService's reaction to <see cref="PaymentCompletedEvent"/>: translate to a
/// <see cref="CreateShipmentCommand"/> and let Wolverine dispatch it to <c>CreateShipmentHandler</c>.
///
/// <para>
/// Same Wolverine cascading-messages pattern used in PaymentService's <c>OrderPlacedHandler</c>:
/// returning the command from the handler is enough — Wolverine sees the return type and
/// invokes the appropriate handler next. The thin translator pattern keeps event-handling and
/// command-handling separate concerns: <c>CreateShipmentHandler</c> owns the work and is also
/// reachable from any future direct trigger.
/// </para>
/// </summary>
public static class PaymentCompletedHandler
{
    public static CreateShipmentCommand Handle(PaymentCompletedEvent @event)
        => new(@event.OrderId);
}
