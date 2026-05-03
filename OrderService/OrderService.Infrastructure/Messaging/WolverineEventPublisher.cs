using OrderService.Domain.Interfaces;
using Wolverine;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Adapter from the domain's <see cref="IEventPublisher"/> abstraction to Wolverine's
/// <see cref="IMessageBus"/>. Application handlers depend on the abstraction (which lives in
/// <c>OrderService.Domain.Interfaces</c>); this implementation lives in Infrastructure where
/// it can reference Wolverine.
///
/// <para>
/// <b>Why a thin pass-through:</b> Wolverine's <c>IMessageBus.PublishAsync</c> already does
/// what we need — when the surrounding handler is wrapped in Wolverine's transactional outbox
/// middleware (<c>AutoApplyTransactions()</c> + <c>UseDurableOutboxOnAllSendingEndpoints()</c>),
/// the call stages the message into the <c>wolverine.outgoing_envelopes</c> table in the same
/// DB transaction as the entity write. After the transaction commits, a background dispatcher
/// flushes the message to Service Bus.
/// </para>
/// <para>
/// <b>Why keep the abstraction at all if it's a one-line pass-through?</b> Two reasons:
/// </para>
/// <list type="bullet">
///   <item><b>Domain layer cleanliness</b> — the <c>OrderService.Application</c> project doesn't
///         reference Wolverine directly; only Infrastructure does. If we ever swap messaging
///         frameworks, only this file changes.</item>
///   <item><b>Testability</b> — handler tests substitute a mock <see cref="IEventPublisher"/>
///         to assert "this command publishes the right event" without spinning up Wolverine.</item>
/// </list>
/// <para>
/// Topic routing (which event goes to which Service Bus topic) is configured once in
/// <c>Program.cs</c> via <c>opts.PublishMessage&lt;T&gt;().ToAzureServiceBusTopic(...)</c>, not
/// here — the publisher just sends; routing is policy.
/// </para>
/// </summary>
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
        => bus.PublishAsync(@event).AsTask();
}
