using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.Events;
using ShippingService.Domain;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Features;

/// <summary>
/// "Create shipment" vertical slice: the command + its handler co-located. Invoked by
/// <see cref="PaymentCompletedHandler"/> when payment confirmation arrives. Creates a Shipment,
/// immediately dispatches it (in our simulated world there's no warehouse step), and publishes
/// <see cref="ShipmentDispatchedEvent"/> for OrderService and NotificationService.
///
/// <para>
/// <b>Idempotency:</b> existence check by <c>OrderId</c> first. Backed by the unique index in
/// <c>ShippingDbContext</c>, the same defense-in-depth pattern used in PaymentService. If two
/// at-least-once redeliveries race past the pre-check, the unique-OrderId index trips
/// <see cref="DbUpdateException"/> on the loser's <c>SaveChangesAsync</c>; we catch it,
/// re-fetch the winning Shipment, and return its ID. Net: at-least-once delivery still
/// produces exactly one Shipment per order.
/// </para>
/// <para>
/// <b>Why a random carrier:</b> simulation only — see <see cref="Carriers"/> below. Real
/// carrier assignment would be a routing decision based on origin, destination, weight, etc.
/// </para>
/// </summary>
public record CreateShipmentCommand(Guid OrderId, Guid BuyerId);

public class CreateShipmentHandler(
    ShippingDbContext context,
    IEventPublisher eventPublisher)
{
    // Placeholder carrier list — picked randomly per shipment for demo purposes.
    private static readonly string[] Carriers = ["FedEx", "UPS", "USPS", "DHL"];

    private static readonly Counter<long> ShipmentsDispatched =
        new Meter("NextAurora").CreateCounter<long>("shipments.dispatched");

    public async Task<Guid> HandleAsync(CreateShipmentCommand request, CancellationToken cancellationToken)
    {
        var existing = await context.Shipments
            .FirstOrDefaultAsync(s => s.OrderId == request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

        var carrier = Carriers[Random.Shared.Next(Carriers.Length)];

        // Two domain operations on the new aggregate: Create (Created state) and Dispatch
        // (Created → Dispatched). Both before persisting — a single SaveChanges captures the
        // full state transition. Note Dispatch() also adds a TrackingEvent automatically, so
        // the audit trail is in place from the first save.
        var shipment = Shipment.Create(request.OrderId, request.BuyerId, carrier);
        shipment.Dispatch();

        await context.Shipments.AddAsync(shipment, cancellationToken);

        // Cross-service event. Wolverine's AutoApplyTransactions wraps the SaveChanges below
        // around both the shipment write and the staged ShipmentDispatchedEvent envelope —
        // no risk of "shipped but no one heard about it".
        await eventPublisher.PublishAsync(new ShipmentDispatchedEvent
        {
            ShipmentId = shipment.Id,
            OrderId = shipment.OrderId,
            BuyerId = shipment.BuyerId,
            Carrier = shipment.Carrier,
            TrackingNumber = shipment.TrackingNumber,
            DispatchedAt = shipment.DispatchedAt!.Value
        }, cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The pre-check above races with concurrent at-least-once redeliveries: two
            // messages can both see "no existing shipment" and both try to insert. The
            // unique index on OrderId catches the loser. Detach our about-to-be-orphaned
            // entity, re-fetch the winner, and return its ID. Without this catch the
            // redelivery model would leak DbUpdateException to Wolverine's retry loop on
            // every concurrent insert. The staged ShipmentDispatchedEvent envelope rolls
            // back with the failed SaveChanges (Wolverine's UseEntityFrameworkCoreTransactions
            // bridge), so the loser doesn't double-publish.
            context.Entry(shipment).State = EntityState.Detached;
            var racedExisting = await context.Shipments
                .FirstOrDefaultAsync(s => s.OrderId == request.OrderId, cancellationToken);
            if (racedExisting is not null)
                return racedExisting.Id;
            throw;
        }

        ShipmentsDispatched.Add(1);
        return shipment.Id;
    }
}
