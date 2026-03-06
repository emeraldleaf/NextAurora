using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.Messaging;

namespace PaymentService.Infrastructure.EventLog;

public class LoggingEventPublisher(
    ServiceBusEventPublisher inner,
    PaymentDbContext dbContext,
    ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        var payload = JsonSerializer.Serialize(@event);
        var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
            ?? Activity.Current?.TraceId.ToString();
        var entityId = ExtractEntityId(payload);
        var eventType = typeof(T).Name;

        var entry = EventLogEntry.Create(eventType, topicName, payload, correlationId, entityId);
        dbContext.EventLogs.Add(entry);
        await dbContext.SaveChangesAsync(ct);

        var succeeded = false;
        try
        {
            await inner.PublishAsync(@event, topicName, ct);
            entry.SetPublished();
            await dbContext.SaveChangesAsync(ct);
            succeeded = true;
        }
        finally
        {
            if (!succeeded)
                logger.LogError("Failed to publish {EventType} to {Topic}. EventLogId={EventLogId} — PublishedAt will remain null.", eventType, topicName, entry.Id);
        }
    }

    private static string? ExtractEntityId(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var name in new[] { "OrderId", "PaymentId", "ShipmentId" })
            {
                if (doc.RootElement.TryGetProperty(name, out var prop))
                    return prop.GetString() ?? prop.ToString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
