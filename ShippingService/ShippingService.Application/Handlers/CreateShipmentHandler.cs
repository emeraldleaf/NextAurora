using System.Diagnostics.Metrics;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Application.Handlers;

/// <summary>
/// Handles the <see cref="CreateShipmentCommand"/>, invoked by <c>PaymentCompletedHandler</c> in
/// this service when payment confirmation arrives. Creates a Shipment, immediately dispatches
/// it (in our simulated world there's no warehouse step), and publishes
/// <see cref="ShipmentDispatchedEvent"/> for OrderService and NotificationService.
///
/// <para>
/// <b>Idempotency:</b> existence check by <c>OrderId</c> first. Backed by the unique index in
/// <c>ShippingDbContext</c>, the same defense-in-depth pattern used in PaymentService.
/// </para>
/// <para>
/// <b>Why a random carrier:</b> simulation only — see <see cref="Carriers"/> below. Real
/// carrier assignment would be a routing decision based on origin, destination, weight, etc.
/// </para>
/// </summary>
public class CreateShipmentHandler(
    IShipmentRepository repository,
    IEventPublisher eventPublisher)
{
    // Placeholder carrier list — picked randomly per shipment for demo purposes.
    private static readonly string[] Carriers = ["FedEx", "UPS", "USPS", "DHL"];

    private static readonly Counter<long> ShipmentsDispatched =
        new Meter("NextAurora").CreateCounter<long>("shipments.dispatched");

    public async Task<Guid> Handle(CreateShipmentCommand request, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

        var carrier = Carriers[Random.Shared.Next(Carriers.Length)];

        // Two domain operations on the new aggregate: Create (Created state) and Dispatch
        // (Created → Dispatched). Both before persisting — a single SaveChanges captures the
        // full state transition. Note Dispatch() also adds a TrackingEvent automatically, so
        // the audit trail is in place from the first save.
        var shipment = Shipment.Create(request.OrderId, carrier);
        shipment.Dispatch();

        await repository.AddAsync(shipment, cancellationToken);

        // Cross-service event. Wolverine's outbox stages this in the same transaction as the
        // shipment write — no risk of "shipped but no one heard about it".
        await eventPublisher.PublishAsync(new ShipmentDispatchedEvent
        {
            ShipmentId = shipment.Id,
            OrderId = shipment.OrderId,
            Carrier = shipment.Carrier,
            TrackingNumber = shipment.TrackingNumber,
            DispatchedAt = shipment.DispatchedAt!.Value
        }, cancellationToken);

        ShipmentsDispatched.Add(1);
        return shipment.Id;
    }
}
