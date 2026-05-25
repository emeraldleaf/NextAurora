using System.Security.Claims;
using NextAurora.Contracts.DTOs;
using ShippingService.Features;
using Wolverine;

namespace ShippingService.Endpoints;

/// <summary>
/// HTTP endpoint for ShippingService. Read-only — shipments are created by the saga
/// (<c>PaymentCompletedEvent</c> → <c>CreateShipmentCommand</c>), never directly by a client.
///
/// <para>
/// All operations require auth AND a buyer-scope check: the JWT subject must match the
/// shipment's <c>BuyerId</c> (denormalized from the originating order via the saga). The
/// principal claim is read here at the HTTP boundary and passed into the query as
/// <see cref="GetShipmentByOrderQuery.RequestingBuyerId"/>; the handler performs the actual
/// ownership comparison and returns null for non-owners, which we surface as 404 so the API
/// does not leak the existence of other buyers' shipments.
/// </para>
/// </summary>
public static class ShippingEndpoints
{
    public static void MapShippingEndpoints(this WebApplication app)
    {
        var group = app.MapV1ApiGroup("Shipping", "shipments").RequireAuthorization();

        // GET /api/v1/shipments/order/{orderId} — look up shipment by the originating order.
        group.MapGet("/order/{orderId:guid}", async (Guid orderId, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            var jwtSub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (jwtSub is null || !Guid.TryParse(jwtSub, out var requestingBuyerId))
                return Results.Forbid();

            var shipment = await bus.InvokeAsync<ShipmentDto?>(
                new GetShipmentByOrderQuery(orderId, requestingBuyerId), ct);
            return shipment is not null ? Results.Ok(shipment) : Results.NotFound();
        });
    }
}
