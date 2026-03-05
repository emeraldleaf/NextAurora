namespace NovaCraft.Contracts.Events;

public record ShipmentDispatchedEvent
{
    public Guid ShipmentId { get; init; }
    public Guid OrderId { get; init; }
    public string Carrier { get; init; } = "";
    public string TrackingNumber { get; init; } = "";
    public DateTime DispatchedAt { get; init; }
}
