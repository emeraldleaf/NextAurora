using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Features;

/// <summary>
/// "Get product by ID" vertical slice. Read-only, cached via <see cref="IProductCache"/>
/// (HybridCache: L1 in-process MemoryCache + L2 distributed Redis).
///
/// <para>
/// <b>Why the cache owns the cache-aside flow:</b> the .NET 10 <c>HybridCache</c> primitive
/// underneath <c>IProductCache</c> has stampede protection — concurrent misses for the same
/// key invoke the factory once. If we did the cache-aside dance inline (try-cache, miss, load,
/// set), every concurrent miss would hit the DB independently.
/// </para>
/// <para>
/// <b>Projection-in-EF on cache miss.</b> The factory hits <c>CatalogDbContext</c> directly with
/// <c>AsNoTracking().Select(...)</c> into <see cref="ProductDto"/> — no entity ever materializes
/// on the read path, no entity-to-DTO mapper, no parent-cartesian rows from the Category JOIN
/// (we read <c>Category.Name</c> as a scalar). Per CLAUDE.md "Performance Rules → EF Core reads".
/// </para>
/// <para>
/// <b>Negative caching:</b> if the projection returns null, the cache stores null. Subsequent
/// lookups for that ID skip the DB. For our system this is fine — product IDs are server-
/// generated GUIDs, so a "not found right now, but will exist later" race is effectively
/// impossible.
/// </para>
/// </summary>
public record GetProductByIdQuery(Guid ProductId);

public class GetProductByIdHandler(CatalogDbContext context, IProductCache cache)
{
    public Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken)
        => cache.GetOrLoadAsync(
            request.ProductId,
            ct => context.Products.AsNoTracking()
                .Where(p => p.Id == request.ProductId)
                .Select(p => new ProductDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description,
                    Price = p.Price,
                    Currency = p.Currency,
                    Category = p.Category != null ? p.Category.Name : "",
                    SellerId = p.SellerId,
                    StockQuantity = p.StockQuantity,
                    IsAvailable = p.IsAvailable
                })
                .FirstOrDefaultAsync(ct),
            cancellationToken);
}
