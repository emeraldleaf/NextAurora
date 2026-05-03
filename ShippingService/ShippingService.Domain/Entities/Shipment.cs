namespace ShippingService.Domain.Entities;

/// <summary>
/// The Shipment aggregate root. Created when <c>PaymentCompletedEvent</c> is received from the
/// PaymentService — that's the saga handoff into shipping. Owns its own list of
/// <see cref="TrackingEvent"/>s as a child collection (added internally on each state transition,
/// never directly by application code).
///
/// <para>
/// State machine: <c>Created → Dispatched → InTransit → Delivered</c>. Each transition is one-way
/// and gated by a status guard, mirroring the pattern used in <see cref="Order"/> and
/// <see cref="Payment"/>. The <c>OrderId</c> column has a unique index in the DbContext —
/// one shipment per order, enforced at the database level.
/// </para>
/// </summary>
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

    // Mutable list because TrackingEvents are added internally as the shipment progresses
    // (see AddTrackingEvent below). Application code outside this aggregate should never
    // touch this collection directly — go through Dispatch/MarkInTransit/MarkDelivered.
    public List<TrackingEvent> TrackingEvents { get; private set; } = [];

    private Shipment() { }

    /// <summary>
    /// Factory: generates a tracking number locally (`NVC-...` prefix). In production this would
    /// usually come from the carrier's API after a label-creation call; we generate here as a
    /// placeholder so the domain remains decoupled from carrier integration code.
    /// </summary>
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

    // Private helper: state transitions automatically log a tracking event for audit/customer
    // visibility. This is why the public methods don't take a description — keeping the audit
    // log consistent is the aggregate's job, not the caller's.
    private void AddTrackingEvent(string description)
    {
        TrackingEvents.Add(TrackingEvent.Create(Id, description, Status.ToString()));
    }
}
