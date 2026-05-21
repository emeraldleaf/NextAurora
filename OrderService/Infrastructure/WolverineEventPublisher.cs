using OrderService.Domain;
using Wolverine;

namespace OrderService.Infrastructure;

/// <summary>
/// Adapter from the domain's <see cref="IEventPublisher"/> abstraction to Wolverine's
/// <see cref="IMessageBus"/>. Feature handlers depend on the abstraction; this implementation
/// lives in Infrastructure where it can reference Wolverine.
///
/// <para>
/// <b>Why a thin pass-through:</b> Wolverine's <c>IMessageBus.PublishAsync</c> already does
/// what we need — when the surrounding handler is wrapped in Wolverine's transactional outbox
/// middleware (<c>AutoApplyTransactions()</c> + <c>UseDurableOutboxOnAllSendingEndpoints()</c>),
/// the call stages the message into the <c>wolverine.outgoing_envelopes</c> table in the same
/// DB transaction as the entity write.
/// </para>
/// <para>
/// <b>Why keep the abstraction at all if it's a one-line pass-through?</b> Test substitution:
/// feature tests substitute a mock <see cref="IEventPublisher"/> to assert "this handler
/// publishes the right event" without spinning up Wolverine. See CLAUDE.md "Interfaces earn
/// their keep through consumer substitution."
/// </para>
/// </summary>
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
        => bus.PublishAsync(@event).AsTask();
}
