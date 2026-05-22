using System.Security.Claims;
using CatalogService.Application.Commands;
using CatalogService.Application.Queries;
using Wolverine;

namespace CatalogService.Api.Endpoints;

/// <summary>
/// HTTP endpoint registrations for CatalogService — the public REST surface for products.
///
/// <para>
/// <b>Endpoint pattern:</b> every Minimal API handler in this file is a thin shim. It accepts
/// query/route/body parameters, builds a Command or Query, dispatches via Wolverine's
/// <see cref="IMessageBus"/>, and translates the result back to an HTTP response. Zero business
/// logic in here — that lives in the handlers in <c>CatalogService.Application</c>.
/// </para>
/// <para>
/// <b>Why this thinness matters (SOLID — SRP):</b> the same command can be invoked from a
/// future gRPC method, a Wolverine event reaction, an admin CLI, or a scheduled job — and
/// none of them care that this HTTP endpoint exists. The endpoint adapts HTTP to commands;
/// commands are the real abstraction.
/// </para>
/// <para>
/// <b>Versioning:</b> registered via <see cref="Microsoft.Extensions.Hosting.Extensions.MapV1ApiGroup"/>,
/// which roots all routes at <c>/api/v1/products</c> and applies <c>HasApiVersion(1.0)</c>.
/// See <c>NextAurora.ServiceDefaults.Extensions</c> for the helper's full documentation.
/// </para>
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapV1ApiGroup("Catalog", "products");

        // GET /api/v1/products — paginated catalog listing. No auth: products are publicly browsable.
        group.MapGet("/", async (IMessageBus bus, CancellationToken ct, int page = 1, int pageSize = 50) =>
        {
            var (p, ps) = ClampPaging(page, pageSize);
            var products = await bus.InvokeAsync<IReadOnlyList<NextAurora.Contracts.DTOs.ProductDto>>(new GetAllProductsQuery(p, ps), ct);
            return Results.Ok(products);
        });

        // GET /api/v1/products/{id}
        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            var product = await bus.InvokeAsync<NextAurora.Contracts.DTOs.ProductDto?>(new GetProductByIdQuery(id), ct);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        // GET /api/v1/products/search?query=...
        // Rate-limited: search is a relatively expensive operation (LIKE %query% on Name and
        // Description) and we don't want a single client hammering it. The "search" rate-limit
        // policy is registered in Program.cs.
        group.MapGet("/search", async (string query, IMessageBus bus, CancellationToken ct, int page = 1, int pageSize = 50) =>
        {
            // Length cap: a 1MB search query string would force the DB to do useless work
            // before returning zero results. Reject early.
            if (query.Length > 200)
                return Results.BadRequest("Search query must not exceed 200 characters.");

            var (p, ps) = ClampPaging(page, pageSize);
            var products = await bus.InvokeAsync<IReadOnlyList<NextAurora.Contracts.DTOs.ProductDto>>(new SearchProductsQuery(query, p, ps), ct);
            return Results.Ok(products);
        }).RequireRateLimiting("search");

        // POST /api/v1/products — seller writes. Auth required, plus seller-scope check
        // mirroring OrderService's buyer-scope pattern: JWT subject must equal command.SellerId.
        group.MapPost("/", async (CreateProductCommand command, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            var jwtSub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (jwtSub is null || !string.Equals(jwtSub, command.SellerId, StringComparison.Ordinal))
                return Results.Forbid();

            var productId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Created($"/api/v1/products/{productId}", new { Id = productId });
        }).RequireAuthorization();

        // PUT /api/v1/products/{id} — seller edit. Two-tier ownership check:
        //  1. Endpoint here: JWT subject must equal command.SellerId.
        //  2. Handler: stored product.SellerId must equal command.SellerId.
        // Both are required: (1) alone lets a caller pair their own seller id with someone else's
        // product id; (2) alone trusts the principal claim from a non-authenticated entry point.
        group.MapPut("/{id:guid}", async (Guid id, UpdateProductCommand command, HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            // Defense-in-depth: if route ID and body ID disagree, refuse rather than guess.
            // Prevents accidental cross-resource updates.
            if (id != command.ProductId) return Results.BadRequest();

            var jwtSub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (jwtSub is null || !string.Equals(jwtSub, command.SellerId, StringComparison.Ordinal))
                return Results.Forbid();

            await bus.InvokeAsync(command, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    // Page numbers always ≥ 1; pageSize clamped to [1, 100] with a default of 50.
    // Keeping this here (not pushed down into the query) ensures the clamping is a request-layer
    // concern — handlers can trust their inputs are sane.
    private static (int page, int pageSize) ClampPaging(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize is < 1 or > 100 ? 50 : pageSize);
}
