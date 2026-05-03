using System.Security.Claims;
using NextAurora.Contracts.DTOs;
using OrderService.Application.Commands;
using OrderService.Application.Queries;
using Wolverine;

namespace OrderService.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders")
            .RequireAuthorization();

        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus) =>
        {
            var order = await bus.InvokeAsync<OrderSummaryDto?>(new GetOrderByIdQuery(id));
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        group.MapGet("/buyer/{buyerId:guid}", async (Guid buyerId, HttpContext context, IMessageBus bus) =>
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null || !Guid.TryParse(userId, out var authenticatedId) || authenticatedId != buyerId)
                return Results.Forbid();

            var orders = await bus.InvokeAsync<IReadOnlyList<OrderSummaryDto>>(new GetOrdersByBuyerQuery(buyerId));
            return Results.Ok(orders);
        });

        group.MapPost("/", async (PlaceOrderCommand command, HttpContext context, IMessageBus bus) =>
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null || !Guid.TryParse(userId, out var authenticatedId) || authenticatedId != command.BuyerId)
                return Results.Forbid();

            var orderId = await bus.InvokeAsync<Guid>(command);
            return Results.Accepted($"/api/orders/{orderId}", new { Id = orderId });
        });
    }
}
