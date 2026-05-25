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
/// <b>Ownership check (IDOR prevention).</b> The handler projects to <see cref="ShipmentDto"/>
/// inline via <c>AsNoTracking() + .Select(...)</c> (no entity materialization), then compares
/// <see cref="ShipmentDto.BuyerId"/> against <see cref="GetShipmentByOrderQuery.RequestingBuyerId"/>
/// (filled by the endpoint from the JWT subject claim). On mismatch the handler returns
/// <c>null</c> — indistinguishable from "shipment not found" — so the API never leaks the
/// existence of other buyers' shipments. The endpoint translates <c>null</c> to 404.
/// </para>
/// </summary>
public record GetShipmentByOrderQuery(Guid OrderId, Guid RequestingBuyerId);

public class GetShipmentByOrderHandler(ShippingDbContext context)
{
    public async Task<ShipmentDto?> HandleAsync(GetShipmentByOrderQuery request, CancellationToken cancellationToken)
    {
        var shipment = await context.Shipments.AsNoTracking()
            .Where(s => s.OrderId == request.OrderId)
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

        if (shipment is null) return null;

        // Ownership guard: caller must be the buyer who placed the originating order.
        // Returning null (translated to 404 by the endpoint) hides the shipment's existence
        // from non-owners. Check happens on the DTO — entity never materializes.
        return shipment.BuyerId == request.RequestingBuyerId ? shipment : null;
    }
}
