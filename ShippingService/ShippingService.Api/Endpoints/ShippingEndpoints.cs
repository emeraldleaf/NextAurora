using ShippingService.Application.Queries;
using Wolverine;

namespace ShippingService.Api.Endpoints;

public static class ShippingEndpoints
{
    public static void MapShippingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shipments").WithTags("Shipping");

        group.MapGet("/order/{orderId:guid}", async (Guid orderId, IMessageBus bus) =>
        {
            var shipment = await bus.InvokeAsync<ShipmentDto?>(new GetShipmentByOrderQuery(orderId));
            return shipment is not null ? Results.Ok(shipment) : Results.NotFound();
        });
    }
}
