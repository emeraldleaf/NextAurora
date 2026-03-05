using CatalogService.Application.Commands;
using CatalogService.Application.Queries;
using MediatR;

namespace CatalogService.Api.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/products").WithTags("Catalog");

        group.MapGet("/", async (IMediator mediator) =>
        {
            var products = await mediator.Send(new GetAllProductsQuery());
            return Results.Ok(products);
        });

        group.MapGet("/{id:guid}", async (Guid id, IMediator mediator) =>
        {
            var product = await mediator.Send(new GetProductByIdQuery(id));
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        group.MapGet("/search", async (string query, IMediator mediator) =>
        {
            var products = await mediator.Send(new SearchProductsQuery(query));
            return Results.Ok(products);
        });

        group.MapPost("/", async (CreateProductCommand command, IMediator mediator) =>
        {
            var productId = await mediator.Send(command);
            return Results.Created($"/api/products/{productId}", new { Id = productId });
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateProductCommand command, IMediator mediator) =>
        {
            if (id != command.ProductId) return Results.BadRequest();
            await mediator.Send(command);
            return Results.NoContent();
        });
    }
}
