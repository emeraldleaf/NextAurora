using CatalogService.Application.Commands;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Interfaces;

namespace CatalogService.Application.Handlers;

/// <summary>
/// Updates a product's editable details (name, description, price). Cache invalidation runs
/// AFTER the DB write succeeds — invalidating before the save would create a window where the
/// cache could be repopulated with the OLD value by a concurrent read.
///
/// <para>
/// Per CLAUDE.md "Performance Rules", cache invalidation belongs in the write path, not "later"
/// or "via TTL". TTL is the safety net for the small race window between save and invalidate;
/// it does not replace this call.
/// </para>
/// </summary>
public class UpdateProductHandler(IProductRepository repository, IProductCache cache)
{
    public async Task HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {request.ProductId} not found");

        product.UpdateDetails(request.Name, request.Description, request.Price);
        await repository.UpdateAsync(product, cancellationToken);

        // Invalidate AFTER the save so a concurrent reader can't repopulate the cache with the
        // pre-update DTO between our invalidate and our save.
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
    }
}
