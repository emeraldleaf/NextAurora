using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.Messaging;

namespace PaymentService.Infrastructure.EventLog;

/// <summary>
/// Decorator around ServiceBusEventPublisher that persists a record of every published event
/// to the database before sending it to the Service Bus.
///
/// Decorator pattern:
///   A decorator wraps another object that implements the same interface, adding behaviour
///   before or after the inner call without changing the inner object.  Here:
///     IEventPublisher (interface)
///       ↑ LoggingEventPublisher (this class — adds persistence + audit logging)
///         ↑ ServiceBusEventPublisher (the actual sender)
///   Callers only ever see IEventPublisher; the wrapping is wired up in DependencyInjection.cs.
///
/// Why persist before sending?
///   We save the event record first (status = pending), then send to the bus.  If the bus call
///   fails, the record remains in the database with PublishedAt = null — a clear signal that
///   the event was generated but never delivered.  This supports:
///     • Dead letter investigation: "which events were created but never published?"
///     • Manual replay: the admin endpoint can re-publish any event by ID.
///
/// Two-phase save:
///   Phase 1 — SaveChangesAsync before the bus call: creates the EventLogEntry row.
///   Phase 2 — entry.SetPublished() + SaveChangesAsync after success: stamps the timestamp.
///   If the service crashes between phase 1 and phase 2, the row survives with PublishedAt=null,
///   making it detectable and replayable.
///
/// Error handling:
///   Uses a finally + bool succeeded pattern rather than catch-log-rethrow.
///   catch-log-rethrow would replace the original exception's stack trace and is flagged by
///   SonarAnalyzer S2139.  The finally block logs only on failure; on success it is a no-op.
/// </summary>
public class LoggingEventPublisher(
    ServiceBusEventPublisher inner,
    PaymentDbContext dbContext,
    ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public async Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class
    {
        // Serialise early so we can inspect the payload for the entity ID and store it verbatim.
        var payload = JsonSerializer.Serialize(@event);

        // CorrelationId links this event log row to all other log lines for the same transaction.
        var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
            ?? Activity.Current?.TraceId.ToString();

        // Best-effort extraction of the entity ID (OrderId, PaymentId, etc.) for filtering.
        // If the payload doesn't contain a recognised ID property this returns null — not fatal.
        var entityId = ExtractEntityId(payload);
        var eventType = typeof(T).Name;

        // Phase 1 — persist the intent before anything is sent.
        // If the application crashes after this line, the row exists with PublishedAt = null
        // and can be found and replayed by the admin endpoint.
        var entry = EventLogEntry.Create(eventType, topicName, payload, correlationId, entityId);
        dbContext.EventLogs.Add(entry);
        await dbContext.SaveChangesAsync(ct);

        var succeeded = false;
        try
        {
            // Phase 2 — send to the bus.  If this throws, succeeded stays false.
            await inner.PublishAsync(@event, topicName, ct);

            // Phase 3 — stamp the successful publish time.
            entry.SetPublished();
            await dbContext.SaveChangesAsync(ct);
            succeeded = true;
        }
        finally
        {
            // Log the failure without swallowing or wrapping the exception.
            // The original exception propagates to the caller (handler) unchanged.
            // The EventLogEntry row remains with PublishedAt = null — visible in the admin UI.
            if (!succeeded)
                logger.LogError("Failed to publish {EventType} to {Topic}. EventLogId={EventLogId} — PublishedAt will remain null.", eventType, topicName, entry.Id);
        }
    }

    /// <summary>
    /// Scans a serialised event's JSON for well-known entity ID properties so the event log row
    /// can be filtered by entity (e.g. "show me all events for order abc-123").
    /// Returns null if the payload is malformed or contains none of the recognised properties —
    /// the empty catch block is intentional and safe: a missing entity ID is non-fatal.
    /// </summary>
    private static string? ExtractEntityId(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var name in new[] { "PaymentId", "OrderId", "ShipmentId" })
            {
                if (doc.RootElement.TryGetProperty(name, out var prop))
                    return prop.GetString() ?? prop.ToString();
            }
            return null;
        }
        catch (JsonException)
        {
            // Malformed JSON should not prevent the event from being published.
            // The entity ID column is nullable precisely for this case.
            return null;
        }
    }
}
