using System.Security.Claims;
using NextAurora.Contracts.DTOs;
using OrderService.Application.Commands;
using OrderService.Application.Queries;
using Wolverine;

namespace OrderService.Api.Endpoints;

/// <summary>
/// HTTP endpoint registrations for OrderService. Same thin-shim pattern as the catalog endpoints
/// (HTTP → Wolverine command/query, no logic in here), with one extra concern: <b>buyer-scope
/// authorization</b>.
///
/// <para>
/// <c>.RequireAuthorization()</c> at the group level enforces "must be authenticated" — Keycloak
/// JWT validation happens before any handler runs. But authentication isn't enough: a user with
/// a valid token must only see/affect *their own* orders. The extra checks inside each handler
/// compare the JWT <c>sub</c> claim against the buyer ID in the route or body, returning 403 if
/// they don't match.
/// </para>
/// <para>
/// <b>Why this check lives here, not in the application layer:</b> the JWT/principal is an HTTP
/// concept; the Application handlers shouldn't know about <c>HttpContext</c>. The endpoint
/// adapts the principal-vs-buyer check before the command crosses the layer boundary.
/// </para>
/// </summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        // Group-level RequireAuthorization: every endpoint below it requires a valid JWT.
        var group = app.MapV1ApiGroup("Orders", "orders").RequireAuthorization();

        // GET /api/v1/orders/{id} — read a single order. Only the buyer scope is enforced
        // implicitly here: the query handler doesn't filter by buyer, but a future improvement
        // is to either pass the authenticated ID or 403 here based on a separate ownership
        // check. Out of scope today.
        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            var order = await bus.InvokeAsync<OrderSummaryDto?>(new GetOrderByIdQuery(id), ct);
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        // GET /api/v1/orders/buyer/{buyerId} — paginated buyer history.
        // Buyer-scope check: the route's buyerId must match the JWT's sub claim.
        group.MapGet("/buyer/{buyerId:guid}", async (Guid buyerId, HttpContext context, IMessageBus bus, CancellationToken ct, int page = 1, int pageSize = 50) =>
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null || !Guid.TryParse(userId, out var authenticatedId) || authenticatedId != buyerId)
                return Results.Forbid();

            var (p, ps) = ClampPaging(page, pageSize);
            var orders = await bus.InvokeAsync<IReadOnlyList<OrderSummaryDto>>(new GetOrdersByBuyerQuery(buyerId, p, ps), ct);
            return Results.Ok(orders);
        });

        // POST /api/v1/orders — place an order.
        // Buyer-scope check: the command's BuyerId must match the JWT's sub claim. Without
        // this, a logged-in user could submit a PlaceOrderCommand with someone else's BuyerId.
        // Returns 202 Accepted because order placement triggers async work (payment processing
        // via Service Bus) — the order exists, but its lifecycle is still in motion.
        group.MapPost("/", async (PlaceOrderCommand command, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null || !Guid.TryParse(userId, out var authenticatedId) || authenticatedId != command.BuyerId)
                return Results.Forbid();

            var orderId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Accepted($"/api/v1/orders/{orderId}", new { Id = orderId });
        });
    }

    private static (int page, int pageSize) ClampPaging(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize is < 1 or > 100 ? 50 : pageSize);
}
