using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Features;

/// <summary>
/// "Get shipment by order" vertical slice. Read-only — the shipment was created by the
/// saga (PaymentCompletedEvent → CreateShipmentCommand). OrderId is the natural identifier
/// because callers (a buyer's order detail page) know the order, not the shipment.
///
/// <para>
/// <b>Ownership check (IDOR prevention).</b> The buyer filter is pushed into the EF
/// <c>Where</c> clause itself — both <c>OrderId == request.OrderId</c> AND
/// <c>BuyerId == request.RequestingBuyerId</c> are SQL predicates. A non-owner request
/// returns <c>null</c> straight from the database without the row ever crossing the wire;
/// indistinguishable from "shipment doesn't exist." The endpoint translates <c>null</c> to
/// 404. Previously the projection filtered only by OrderId and the ownership check ran on
/// the materialized DTO — same external contract, but tightening the predicate to SQL
/// avoids transporting non-owner rows in the first place. See CLAUDE.md
/// "Security Requirements" for the canonical anti-enumeration pattern.
/// </para>
/// </summary>
public record GetShipmentByOrderQuery(Guid OrderId, Guid RequestingBuyerId);

public class GetShipmentByOrderHandler(ShippingDbContext context)
{
    public Task<ShipmentDto?> HandleAsync(GetShipmentByOrderQuery request, CancellationToken cancellationToken)
        => context.Shipments.AsNoTracking()
            .Where(s => s.OrderId == request.OrderId && s.BuyerId == request.RequestingBuyerId)
            .Select(s => new ShipmentDto(
                s.Id,
                s.OrderId,
                s.BuyerId,
                s.Carrier,
                s.TrackingNumber,
                s.Status.ToString(),
                s.CreatedAt,
                s.DispatchedAt,
                s.TrackingEvents.Select(e => new TrackingEventDto(e.Description, e.Status, e.OccurredAt)).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
}
