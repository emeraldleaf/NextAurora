using NextAurora.Contracts.Events;

namespace PaymentService.Features;

/// <summary>
/// PaymentService's reaction to <see cref="OrderPlacedEvent"/>: convert the event into a
/// <see cref="ProcessPaymentCommand"/> and let Wolverine dispatch it to <c>ProcessPaymentHandler</c>.
///
/// <para>
/// <b>Wolverine cascading messages:</b> a handler can return a message (or tuple of messages)
/// instead of <c>void</c>/<c>Task</c>, and Wolverine will publish/invoke whatever is returned.
/// That's why this handler is a static method that just returns the command — Wolverine sees
/// the return type, treats it as a side-effect message, and runs the command's handler next.
/// No <c>IMessageBus.InvokeAsync</c> call needed.
/// </para>
/// <para>
/// <b>Why split event-handling from command-handling at all?</b> Because <c>ProcessPaymentCommand</c>
/// is also reachable from the HTTP endpoint (admin/manual processing). Having one handler that
/// owns the actual work and a thin event-translator on top means the same business logic runs
/// regardless of how processing was triggered. Open/Closed: if a third trigger appears (e.g.
/// scheduled retry job), it dispatches the same command and works with no changes here.
/// </para>
/// </summary>
public static class OrderPlacedHandler
{
    public static ProcessPaymentCommand Handle(OrderPlacedEvent @event)
        => new(@event.OrderId, @event.TotalAmount, @event.Currency, @event.BuyerId);
}
