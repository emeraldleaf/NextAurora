using System.Diagnostics.CodeAnalysis;

namespace ShippingService.Domain;

/// <summary>
/// The Shipment aggregate root. Created when <c>PaymentCompletedEvent</c> is received from the
/// PaymentService — that's the saga handoff into shipping. Owns its own list of
/// <see cref="TrackingEvent"/>s as a child collection (added internally on each state transition,
/// never directly by application code).
///
/// <para>
/// State machine: <c>Created → Dispatched</c> — that's all the *live* transitions today.
/// <see cref="ShipmentStatus"/> also defines <c>InTransit</c> and <c>Delivered</c> as scaffolding,
/// and the DB carries a <see cref="DeliveredAt"/> column, but nothing produces those transitions
/// (no carrier-polling job, no admin endpoint). The previous <c>MarkInTransit</c> /
/// <c>MarkDelivered</c> methods were dead branches — only their own tests exercised them — so
/// they were cut. When a real carrier callback or polling loop lands, add the methods back
/// alongside the producer code, not before. Keeping the enum + column means no DB migration is
/// needed when that day comes.
/// </para>
/// <para>
/// The <c>OrderId</c> column has a unique index in the DbContext — one shipment per order,
/// enforced at the database level.
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
    // Schema-only for now — populated when MarkDelivered exists. See class doc.
    [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Setter is used by EF Core materialization; the writing call site lives in the not-yet-restored MarkDelivered method. See class-level doc.")]
    public DateTime? DeliveredAt { get; private set; }

    // Mutable list because TrackingEvents are added internally on each state transition
    // (see AddTrackingEvent below). Application code outside this aggregate should never
    // touch this collection directly — go through Dispatch().
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

    private void AddTrackingEvent(string description)
    {
        TrackingEvents.Add(TrackingEvent.Create(Id, description, Status.ToString()));
    }
}
