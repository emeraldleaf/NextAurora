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
        if (orderId == Guid.Empty)
            throw new ArgumentException("Order ID must not be empty.", nameof(orderId));

        ArgumentException.ThrowIfNullOrWhiteSpace(carrier);

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
        if (Status != ShipmentStatus.Created)
            throw new InvalidOperationException("Cannot dispatch shipment in the current status.");
        Status = ShipmentStatus.Dispatched;
        DispatchedAt = DateTime.UtcNow;
        AddTrackingEvent("Package dispatched");
    }

    public void MarkInTransit()
    {
        if (Status != ShipmentStatus.Dispatched)
            throw new InvalidOperationException("Cannot mark shipment as in transit in the current status.");
        Status = ShipmentStatus.InTransit;
        AddTrackingEvent("Package in transit");
    }

    public void MarkDelivered()
    {
        if (Status != ShipmentStatus.InTransit)
            throw new InvalidOperationException("Cannot mark shipment as delivered in the current status.");
        Status = ShipmentStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        AddTrackingEvent("Package delivered");
    }

    private void AddTrackingEvent(string description)
    {
        TrackingEvents.Add(TrackingEvent.Create(Id, description, Status.ToString()));
    }
}
