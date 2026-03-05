namespace ShippingService.Domain.Entities;

public class TrackingEvent
{
    public Guid Id { get; private set; }
    public Guid ShipmentId { get; private set; }
    public string Description { get; private set; } = "";
    public string Status { get; private set; } = "";
    public DateTime OccurredAt { get; private set; }

    private TrackingEvent() { }

    public static TrackingEvent Create(Guid shipmentId, string description, string status)
    {
        return new TrackingEvent
        {
            Id = Guid.NewGuid(),
            ShipmentId = shipmentId,
            Description = description,
            Status = status,
            OccurredAt = DateTime.UtcNow
        };
    }
}
