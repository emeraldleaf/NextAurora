using ShippingService.Application.Queries;
using Wolverine;

namespace ShippingService.Api.Endpoints;

/// <summary>
/// HTTP endpoint for ShippingService. Read-only — shipments are created by the saga
/// (<c>PaymentCompletedEvent</c> → <c>CreateShipmentCommand</c>), never directly by a client.
///
/// <para>
/// All operations require auth: tracking info is buyer-specific and shouldn't be publicly
/// browsable. (Strict buyer-scope check would require loading the shipment first to find the
/// owning buyer ID — left as future work; for now <c>RequireAuthorization()</c> + obscure
/// tracking numbers is the bar.)
/// </para>
/// </summary>
public static class ShippingEndpoints
{
    public static void MapShippingEndpoints(this WebApplication app)
    {
        var group = app.MapV1ApiGroup("Shipping", "shipments").RequireAuthorization();

        // GET /api/v1/shipments/order/{orderId} — look up shipment by the originating order.
        // OrderId is the natural identifier here because callers (a buyer's order detail page)
        // know the order, not the shipment.
        group.MapGet("/order/{orderId:guid}", async (Guid orderId, IMessageBus bus, CancellationToken ct) =>
        {
            var shipment = await bus.InvokeAsync<ShipmentDto?>(new GetShipmentByOrderQuery(orderId), ct);
            return shipment is not null ? Results.Ok(shipment) : Results.NotFound();
        });
    }
}
