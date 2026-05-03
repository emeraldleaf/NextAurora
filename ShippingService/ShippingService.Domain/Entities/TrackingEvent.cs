namespace ShippingService.Domain.Entities;

/// <summary>
/// A child entity of <see cref="Shipment"/>: a single audit-log entry recorded each time the
/// parent shipment changes state. Status here is a plain string snapshot of the parent's
/// <see cref="ShipmentStatus"/> at the moment the event was added — string rather than the enum
/// so historical entries don't need to migrate if the enum evolves.
/// </summary>
public class TrackingEvent
{
    public Guid Id { get; private set; }
    public Guid ShipmentId { get; private set; }
    public string Description { get; private set; } = "";
    public string Status { get; private set; } = "";
    public DateTime OccurredAt { get; private set; }

    private TrackingEvent() { }

    // Internal-by-convention factory: only Shipment.AddTrackingEvent should call this.
    // Marked public because Shipment is in the same assembly and we don't use friend-assembly
    // tricks here, but treat as if it were `internal`.
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
