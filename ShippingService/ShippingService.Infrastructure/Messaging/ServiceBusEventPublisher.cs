using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Infrastructure.Messaging;

public class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        var sender = client.CreateSender(topicName);
        var body = JsonSerializer.Serialize(@event);

        var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
            ?? Activity.Current?.TraceId.ToString();

        var message = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name,
            CorrelationId = correlationId
        };

        if (correlationId is not null)
        {
            message.ApplicationProperties["X-Correlation-Id"] = correlationId;
        }

        await sender.SendMessageAsync(message, ct);
    }
}
