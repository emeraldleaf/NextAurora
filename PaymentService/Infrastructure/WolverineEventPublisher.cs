using PaymentService.Domain;
using Wolverine;

namespace PaymentService.Infrastructure;

/// <summary>
/// Adapter from <see cref="IEventPublisher"/> to Wolverine's <see cref="IMessageBus"/>. Every
/// service has the same thin shim so feature code can publish events without referencing
/// Wolverine directly.
///
/// <para>
/// <b>Enlistment caveat (Wolverine 6):</b> a publish through this *constructor*-injected
/// <see cref="IMessageBus"/> is NOT enlisted in the handler's outbox transaction — it dispatches
/// immediately, not staged-then-committed-with-the-entity-write. For a message that MUST be
/// outbox-staged in the same transaction (e.g. a local continuation a downstream handler reads
/// back), publish via a <see cref="IMessageContext"/> injected as a <c>HandleAsync</c> method
/// parameter instead (see PaymentService's <c>ProcessPaymentHandler</c>). This shim stays correct
/// for fire-and-forget external events. See docs/project-decisions.md "Wolverine 5→6 upgrade notes".
/// </para>
/// </summary>
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
    {
        // Wolverine's IMessageBus.PublishAsync has no CancellationToken overload; we honor the
        // request-scoped ct by refusing to stage outbox work for an already-cancelled request.
        ct.ThrowIfCancellationRequested();
        return bus.PublishAsync(@event).AsTask();
    }
}
