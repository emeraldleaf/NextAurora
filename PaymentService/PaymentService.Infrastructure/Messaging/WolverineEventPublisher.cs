using PaymentService.Domain.Interfaces;
using Wolverine;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Adapter from <see cref="IEventPublisher"/> to Wolverine's <see cref="IMessageBus"/>. See
/// <c>OrderService.Infrastructure.Messaging.WolverineEventPublisher</c> for the full
/// rationale — every service has the same thin shim so Application code can publish events
/// without referencing Wolverine directly. With the transactional outbox enabled, this call
/// stages the message in the <c>wolverine</c> schema in the same DB transaction as the entity
/// write.
/// </summary>
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
        => bus.PublishAsync(@event).AsTask();
}
