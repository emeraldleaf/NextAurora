using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using CatalogService.Domain.Interfaces;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

/// <summary>
/// Read-side handler for <see cref="GetProductByIdQuery"/>. Cache-aside via
/// <see cref="IProductCache"/>:
/// <list type="number">
///   <item>Try the cache. Hit → return immediately, no DB roundtrip.</item>
///   <item>Miss → load from the repository, project to DTO, populate the cache, return.</item>
/// </list>
///
/// <para>
/// <b>Why cache the DTO and not the entity:</b> entities are mutable + tracked. The
/// projection-as-cache shape keeps deserialization simple and means the cached unit is exactly
/// what the endpoint returns. See <see cref="IProductCache"/> for the full contract.
/// </para>
/// <para>
/// <b>What happens on cache write failure:</b> the <c>SetAsync</c> call can throw if Redis is
/// unreachable. We let it surface — a hard fail is better than silent slow degradation, and
/// the orchestrator's health checks will route traffic away if Redis is genuinely down. The
/// alternative (catch + log + carry on) makes the failure invisible and harder to diagnose.
/// </para>
/// </summary>
public class GetProductByIdHandler(IProductRepository repository, IProductCache cache)
{
    public async Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(request.ProductId, cancellationToken);
        if (cached is not null) return cached;

        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null) return null;

        var dto = new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Currency = product.Currency,
            Category = product.Category?.Name ?? "",
            SellerId = product.SellerId,
            StockQuantity = product.StockQuantity,
            IsAvailable = product.IsAvailable
        };

        await cache.SetAsync(dto, cancellationToken);
        return dto;
    }
}
