using MediatR;
using ShippingService.Application.Queries;

namespace ShippingService.Api.Endpoints;

public static class ShippingEndpoints
{
    public static void MapShippingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shipments").WithTags("Shipping");

        group.MapGet("/order/{orderId:guid}", async (Guid orderId, IMediator mediator) =>
        {
            var shipment = await mediator.Send(new GetShipmentByOrderQuery(orderId));
            return shipment is not null ? Results.Ok(shipment) : Results.NotFound();
        });
    }
}
