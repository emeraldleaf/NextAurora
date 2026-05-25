using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

/// <summary>
/// Read-side handler for <see cref="GetProductByIdQuery"/>. Delegates to
/// <see cref="IProductCache"/> with a factory that hits <see cref="IProductReadStore"/> on
/// miss — projection-in-EF, no entity ever materializes on the read path.
///
/// <para>
/// <b>Why the cache owns the cache-aside flow:</b> the .NET 10 <c>HybridCache</c> primitive
/// underneath <c>IProductCache</c> has stampede protection — concurrent misses for the same
/// key invoke the factory once. If we did the cache-aside dance inline here (try-cache, miss,
/// load, set), we'd lose that protection: every concurrent miss would hit the DB independently.
/// </para>
/// <para>
/// <b>Negative caching.</b> If <see cref="IProductReadStore.GetByIdAsync"/> returns null, the
/// factory returns null and the cache stores it. Subsequent lookups for that ID skip the DB.
/// For our system this is fine: product IDs are server-generated GUIDs, so a "not found right
/// now, but will exist later" race is effectively impossible.
/// </para>
/// </summary>
public class GetProductByIdHandler(IProductReadStore readStore, IProductCache cache)
{
    public Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken)
        => cache.GetOrLoadAsync(
            request.ProductId,
            ct => readStore.GetByIdAsync(request.ProductId, ct),
            cancellationToken);
}
