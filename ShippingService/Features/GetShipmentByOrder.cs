using NextAurora.Contracts.DTOs;
using ShippingService.Domain;

namespace ShippingService.Features;

/// <summary>
/// "Get shipment by order" vertical slice. Read-only — the shipment was created by the
/// saga (PaymentCompletedEvent → CreateShipmentCommand). OrderId is the natural identifier
/// because callers (a buyer's order detail page) know the order, not the shipment.
///
/// <para>
/// <b>Ownership check (IDOR prevention).</b> The handler loads
/// <see cref="ShipmentDto"/> via <see cref="IShipmentRepository.GetSummaryByOrderIdAsync"/>
/// (projection-in-EF — no entity materialization), then compares
/// <see cref="ShipmentDto.BuyerId"/> against <see cref="GetShipmentByOrderQuery.RequestingBuyerId"/>
/// (filled by the endpoint from the JWT subject claim). On mismatch the handler returns
/// <c>null</c> — indistinguishable from "shipment not found" — so the API never leaks the
/// existence of other buyers' shipments. The endpoint translates <c>null</c> to 404.
/// </para>
/// </summary>
public record GetShipmentByOrderQuery(Guid OrderId, Guid RequestingBuyerId);

public class GetShipmentByOrderHandler(IShipmentRepository repository)
{
    public async Task<ShipmentDto?> HandleAsync(GetShipmentByOrderQuery request, CancellationToken cancellationToken)
    {
        var shipment = await repository.GetSummaryByOrderIdAsync(request.OrderId, cancellationToken);
        if (shipment is null) return null;

        // Ownership guard: caller must be the buyer who placed the originating order.
        // Returning null (translated to 404 by the endpoint) hides the shipment's existence
        // from non-owners. Check happens after the projection — the entity never materializes.
        return shipment.BuyerId == request.RequestingBuyerId ? shipment : null;
    }
}
