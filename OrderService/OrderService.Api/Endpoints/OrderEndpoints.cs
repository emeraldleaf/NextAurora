using NextAurora.Contracts.DTOs;
using OrderService.Application.Commands;
using OrderService.Application.Queries;
using Wolverine;

namespace OrderService.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus) =>
        {
            var order = await bus.InvokeAsync<OrderSummaryDto?>(new GetOrderByIdQuery(id));
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        group.MapGet("/buyer/{buyerId:guid}", async (Guid buyerId, IMessageBus bus) =>
        {
            var orders = await bus.InvokeAsync<IReadOnlyList<OrderSummaryDto>>(new GetOrdersByBuyerQuery(buyerId));
            return Results.Ok(orders);
        });

        group.MapPost("/", async (PlaceOrderCommand command, IMessageBus bus) =>
        {
            var orderId = await bus.InvokeAsync<Guid>(command);
            return Results.Accepted($"/api/orders/{orderId}", new { Id = orderId });
        });
    }
}
