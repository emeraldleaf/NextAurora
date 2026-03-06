using CatalogService.Application.Commands;
using CatalogService.Application.Queries;
using Wolverine;

namespace CatalogService.Api.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/products").WithTags("Catalog");

        group.MapGet("/", async (IMessageBus bus) =>
        {
            var products = await bus.InvokeAsync<IReadOnlyList<NextAurora.Contracts.DTOs.ProductDto>>(new GetAllProductsQuery());
            return Results.Ok(products);
        });

        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus) =>
        {
            var product = await bus.InvokeAsync<NextAurora.Contracts.DTOs.ProductDto?>(new GetProductByIdQuery(id));
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        group.MapGet("/search", async (string query, IMessageBus bus) =>
        {
            var products = await bus.InvokeAsync<IReadOnlyList<NextAurora.Contracts.DTOs.ProductDto>>(new SearchProductsQuery(query));
            return Results.Ok(products);
        });

        group.MapPost("/", async (CreateProductCommand command, IMessageBus bus) =>
        {
            var productId = await bus.InvokeAsync<Guid>(command);
            return Results.Created($"/api/products/{productId}", new { Id = productId });
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateProductCommand command, IMessageBus bus) =>
        {
            if (id != command.ProductId) return Results.BadRequest();
            await bus.InvokeAsync(command);
            return Results.NoContent();
        });
    }
}
