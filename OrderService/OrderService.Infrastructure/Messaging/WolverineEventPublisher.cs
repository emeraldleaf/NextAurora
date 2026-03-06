using OrderService.Domain.Interfaces;
using Wolverine;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Bridges the domain IEventPublisher abstraction to Wolverine's IMessageBus.
/// Topic routing is configured in UseAzureServiceBus() in Program.cs via
/// opts.PublishMessage&lt;T&gt;().ToAzureServiceBusTopic(...), not at the call site.
/// </summary>
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
        => bus.PublishAsync(@event).AsTask();
}
