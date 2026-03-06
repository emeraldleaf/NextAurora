namespace OrderService.Infrastructure.EventLog;

public class EventLogEntry
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Topic { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string? CorrelationId { get; private set; }
    public string? EntityId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public bool IsReplay { get; private set; }
    public Guid? OriginalEventId { get; private set; }

    private EventLogEntry() { }

    public static EventLogEntry Create(string eventType, string topic, string payload, string? correlationId, string? entityId) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Topic = topic,
            Payload = payload,
            CorrelationId = correlationId,
            EntityId = entityId,
            OccurredAt = DateTimeOffset.UtcNow
        };

    public static EventLogEntry CreateReplay(string eventType, string topic, string payload, string? correlationId, string? entityId, Guid originalEventId) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Topic = topic,
            Payload = payload,
            CorrelationId = correlationId,
            EntityId = entityId,
            OccurredAt = DateTimeOffset.UtcNow,
            IsReplay = true,
            OriginalEventId = originalEventId
        };

    public void SetPublished() => PublishedAt = DateTimeOffset.UtcNow;
}
