using System.Text.Json;
using Azure.Messaging.ServiceBus;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Infrastructure.Messaging;

public class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        var sender = client.CreateSender(topicName);
        var body = JsonSerializer.Serialize(@event);
        var message = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name
        };
        await sender.SendMessageAsync(message, ct);
    }
}
