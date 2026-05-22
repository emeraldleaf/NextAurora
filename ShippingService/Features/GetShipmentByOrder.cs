using ShippingService.Domain;

namespace ShippingService.Features;

/// <summary>
/// "Get shipment by order" vertical slice: query + DTOs + handler co-located. Read-only — the
/// shipment was created by the saga (PaymentCompletedEvent → CreateShipmentCommand). OrderId is
/// the natural identifier because callers (a buyer's order detail page) know the order, not the
/// shipment.
///
/// <para>
/// <b>Ownership check.</b> <see cref="GetShipmentByOrderQuery.RequestingBuyerId"/> is filled by
/// the endpoint from the JWT subject claim. If the loaded shipment's <c>BuyerId</c> does not
/// match, the handler returns <c>null</c> — indistinguishable from "shipment not found" — so
/// the API does not leak the existence of other buyers' shipments to non-owners. The endpoint
/// translates <c>null</c> to 404.
/// </para>
/// </summary>
public record GetShipmentByOrderQuery(Guid OrderId, Guid RequestingBuyerId);

public record ShipmentDto(
    Guid Id,
    Guid OrderId,
    string Carrier,
    string TrackingNumber,
    string Status,
    DateTime CreatedAt,
    DateTime? DispatchedAt,
    List<TrackingEventDto> TrackingEvents);

public record TrackingEventDto(string Description, string Status, DateTime OccurredAt);

public class GetShipmentByOrderHandler(IShipmentRepository repository)
{
    public async Task<ShipmentDto?> HandleAsync(GetShipmentByOrderQuery request, CancellationToken cancellationToken)
    {
        var shipment = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (shipment is null) return null;

        // Ownership guard: caller must be the buyer who placed the originating order. Returning
        // null (translated to 404 by the endpoint) hides the shipment's existence from non-owners.
        if (shipment.BuyerId != request.RequestingBuyerId)
            return null;

        return new ShipmentDto(
            shipment.Id,
            shipment.OrderId,
            shipment.Carrier,
            shipment.TrackingNumber,
            shipment.Status.ToString(),
            shipment.CreatedAt,
            shipment.DispatchedAt,
            shipment.TrackingEvents.Select(e =>
                new TrackingEventDto(e.Description, e.Status, e.OccurredAt)).ToList());
    }
}
