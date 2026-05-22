using PaymentService.Domain;
using Wolverine;

namespace PaymentService.Infrastructure;

/// <summary>
/// Adapter from <see cref="IEventPublisher"/> to Wolverine's <see cref="IMessageBus"/>. Every
/// service has the same thin shim so feature code can publish events without referencing
/// Wolverine directly. With the transactional outbox enabled, this call stages the message in
/// the <c>wolverine</c> schema in the same DB transaction as the entity write.
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
