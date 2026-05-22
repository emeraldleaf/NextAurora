using NextAurora.Contracts.Events;

namespace ShippingService.Features;

/// <summary>
/// ShippingService's reaction to <see cref="PaymentCompletedEvent"/>: translate to a
/// <see cref="CreateShipmentCommand"/> and let Wolverine dispatch it to <c>CreateShipmentHandler</c>.
///
/// <para>
/// Wolverine cascading-messages pattern: returning the command from the handler is enough —
/// Wolverine sees the return type and invokes the appropriate handler next. The thin translator
/// pattern keeps event-handling and command-handling separate concerns: <c>CreateShipmentHandler</c>
/// owns the work and is also reachable from any future direct trigger.
/// </para>
/// </summary>
public static class PaymentCompletedHandler
{
    public static CreateShipmentCommand Handle(PaymentCompletedEvent @event)
        => new(@event.OrderId, @event.BuyerId);
}
