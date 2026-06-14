using OrderService.Domain;
using Wolverine;

namespace OrderService.Infrastructure;

/// <summary>
/// Adapter from the domain's <see cref="IEventPublisher"/> abstraction to Wolverine's
/// <see cref="IMessageBus"/>. Feature handlers depend on the abstraction; this implementation
/// lives in Infrastructure where it can reference Wolverine.
///
/// <para>
/// <b>Enlistment caveat (Wolverine 6):</b> a publish through this *constructor*-injected
/// <see cref="IMessageBus"/> is NOT enlisted in the handler's outbox transaction — it dispatches
/// immediately rather than staging-then-committing-with-the-entity-write. For a message that MUST
/// be outbox-staged in the same transaction (e.g. a local continuation a downstream handler reads
/// back), publish via a <see cref="IMessageContext"/> injected as a <c>HandleAsync</c> method
/// parameter (see PaymentService's <c>ProcessPaymentHandler</c>). This shim stays correct for
/// fire-and-forget external events; whether OrderService's external publishes need the stricter
/// same-transaction guarantee is a tracked follow-up. See docs/project-decisions.md
/// "Wolverine 5→6 upgrade notes".
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
    {
        // IMessageBus.PublishAsync has no CancellationToken overload. We honor the request-scoped ct
        // by refusing to publish once it has been cancelled (see the enlistment caveat above for the
        // transactional-staging semantics).
        ct.ThrowIfCancellationRequested();
        return bus.PublishAsync(@event).AsTask();
    }
}
