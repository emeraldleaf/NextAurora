using MediatR;
using OrderService.Application.Commands;
using OrderService.Application.Queries;

namespace OrderService.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapGet("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var order = await mediator.Send(new GetOrderByIdQuery(id));
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        group.MapGet("/buyer/{buyerId:guid}", async (Guid buyerId, IMediator mediator) =>
        {
            var orders = await mediator.Send(new GetOrdersByBuyerQuery(buyerId));
            return Results.Ok(orders);
        });

        group.MapPost("/", async (PlaceOrderCommand command, IMediator mediator) =>
        {
            var orderId = await mediator.Send(command);
            return Results.Accepted($"/api/orders/{orderId}", new { Id = orderId });
        });
    }
}
