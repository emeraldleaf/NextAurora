using System.Diagnostics.Metrics;

namespace NextAurora.ServiceDefaults.Metrics;

/// <summary>
/// Shared business metrics counters for NextAurora services.
/// Register as a singleton and inject into handlers that need to record domain events.
/// </summary>
public sealed class NovaCraftMetrics : IDisposable
{
    private readonly Meter _meter = new("NextAurora");

    public Counter<long> OrdersPlaced { get; }
    public Counter<long> PaymentsProcessed { get; }
    public Counter<long> ShipmentsDispatched { get; }
    public Counter<long> NotificationsSent { get; }
    /// <summary>
    /// Incremented whenever a Service Bus message is abandoned (returned for retry or DLQ).
    /// Tagged with Subject and service name. Alert on this counter rising to detect DLQ
    /// pile-ups before they cause user-visible outages.
    /// </summary>
    public Counter<long> MessagesAbandoned { get; }

    public NovaCraftMetrics()
    {
        OrdersPlaced = _meter.CreateCounter<long>("orders.placed", description: "Number of orders successfully placed");
        PaymentsProcessed = _meter.CreateCounter<long>("payments.processed", description: "Number of payments successfully processed");
        ShipmentsDispatched = _meter.CreateCounter<long>("shipments.dispatched", description: "Number of shipments dispatched");
        NotificationsSent = _meter.CreateCounter<long>("notifications.sent", description: "Number of notifications sent");
        MessagesAbandoned = _meter.CreateCounter<long>("messages.abandoned", description: "Messages abandoned for retry or dead-letter queue");
    }

    public void Dispose() => _meter.Dispose();
}
