using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Publishes domain events to Azure Service Bus topics.
///
/// Context propagation:
///   When a user places an order the entire workflow is asynchronous — OrderService, PaymentService,
///   ShippingService, and NotificationService each run independently.  Without extra work, the
///   CorrelationId, UserId, and SessionId established at the HTTP entry point would be lost as
///   soon as the first Service Bus message was sent.
///
///   This class prevents that loss by reading the three identifiers from the current
///   OpenTelemetry Activity's baggage (where CorrelationIdMiddleware stored them) and writing
///   them as ApplicationProperties on every outgoing message:
///     X-Correlation-Id  — joins all log lines for the same transaction across services
///     X-User-Id         — identifies the user who triggered the workflow
///     X-Session-Id      — identifies the browser/app session
///
///   The receiving service's processor reads these properties back out and restores them into
///   its own Activity baggage and logger scope, continuing the chain seamlessly.
///
/// Resource management:
///   ServiceBusSender implements IAsyncDisposable and holds an AMQP link to the broker.
///   'await using' ensures the link is released after each send, preventing connection leaks
///   under sustained load.  For high-throughput scenarios a cached/pooled sender would be more
///   efficient, but per-call disposal is correct and safe for the current volume.
///
/// This class is wrapped by LoggingEventPublisher (Decorator pattern) which persists a record
/// of every published event to the database for audit and replay purposes.
/// </summary>
public class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        // await using ensures DisposeAsync() is called when the method exits, releasing the AMQP link.
        await using var sender = client.CreateSender(topicName);
        var body = JsonSerializer.Serialize(@event);

        // Read context from Activity baggage.  Activity.Current is null when there is no active
        // distributed trace (e.g. unit tests, or if OTel isn't configured) — ?. handles that safely.
        // Prefer "correlation.id" baggage (set explicitly by middleware) over the raw trace ID.
        var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
            ?? Activity.Current?.TraceId.ToString();
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        var message = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            // Subject is the event type name (e.g. "OrderPlacedEvent") — visible in the Azure portal
            // and useful for filtering / routing without deserialising the body.
            Subject = typeof(T).Name,
            // CorrelationId is a first-class Service Bus field; setting it here in addition to the
            // ApplicationProperty ensures compatibility with tooling that reads the standard field.
            CorrelationId = correlationId
        };

        // ApplicationProperties are arbitrary key-value pairs that travel with the message body.
        // The receiving processor reads these to restore context on the other side of the bus.
        // Only set a property when the value exists — an empty string would be misleading.
        if (correlationId is not null) message.ApplicationProperties["X-Correlation-Id"] = correlationId;
        if (userId is not null) message.ApplicationProperties["X-User-Id"] = userId;
        if (sessionId is not null) message.ApplicationProperties["X-Session-Id"] = sessionId;

        await sender.SendMessageAsync(message, ct);
    }
}
