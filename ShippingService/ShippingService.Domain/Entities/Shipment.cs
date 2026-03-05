namespace ShippingService.Domain.Entities;

public class Shipment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Carrier { get; private set; } = "";
    public string TrackingNumber { get; private set; } = "";
    public ShipmentStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? DispatchedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public List<TrackingEvent> TrackingEvents { get; private set; } = [];

    private Shipment() { }

    public static Shipment Create(Guid orderId, string carrier)
    {
        var trackingNumber = $"NVC-{Guid.NewGuid().ToString("N")[..12].ToUpper(System.Globalization.CultureInfo.InvariantCulture)}";
        return new Shipment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = carrier,
            TrackingNumber = trackingNumber,
            Status = ShipmentStatus.Created,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void Dispatch()
    {
        Status = ShipmentStatus.Dispatched;
        DispatchedAt = DateTime.UtcNow;
        AddTrackingEvent("Package dispatched");
    }

    public void MarkInTransit()
    {
        Status = ShipmentStatus.InTransit;
        AddTrackingEvent("Package in transit");
    }

    public void MarkDelivered()
    {
        Status = ShipmentStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        AddTrackingEvent("Package delivered");
    }

    private void AddTrackingEvent(string description)
    {
        TrackingEvents.Add(TrackingEvent.Create(Id, description, Status.ToString()));
    }
}
