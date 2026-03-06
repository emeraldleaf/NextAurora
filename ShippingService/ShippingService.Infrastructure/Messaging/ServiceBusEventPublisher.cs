using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Infrastructure.Messaging;

public class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        await using var sender = client.CreateSender(topicName);
        var body = JsonSerializer.Serialize(@event);

        var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
            ?? Activity.Current?.TraceId.ToString();
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        var message = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name,
            CorrelationId = correlationId
        };

        if (correlationId is not null) message.ApplicationProperties["X-Correlation-Id"] = correlationId;
        if (userId is not null) message.ApplicationProperties["X-User-Id"] = userId;
        if (sessionId is not null) message.ApplicationProperties["X-Session-Id"] = sessionId;

        await sender.SendMessageAsync(message, ct);
    }
}
