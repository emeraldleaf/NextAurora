using CatalogService.Application.Commands;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Interfaces;

namespace CatalogService.Application.Handlers;

/// <summary>
/// Reserves stock for a product as part of order placement. Mutates <c>StockQuantity</c> and
/// (transitively) <c>IsAvailable</c>, both of which are in <see cref="NextAurora.Contracts.DTOs.ProductDto"/>
/// — so any cached entry must be invalidated when stock changes.
///
/// <para>
/// <b>Concurrency story:</b> the actual race protection is the <c>xmin</c> concurrency token
/// on Product (see <c>CatalogDbContext</c>). Two simultaneous reservations can't both win;
/// the loser gets <c>DbUpdateConcurrencyException</c> and returns false. The cache is downstream
/// of that — invalidation runs after a successful save, so the cache can't outlive the
/// concurrency check.
/// </para>
/// </summary>
public class ReserveStockHandler(IProductRepository repository, IProductCache cache)
{
    public async Task<bool> HandleAsync(ReserveStockCommand request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        if (product.StockQuantity < request.Quantity)
            return false;

        product.AdjustStock(product.StockQuantity - request.Quantity);
        await repository.UpdateAsync(product, cancellationToken);

        // Invalidate AFTER the save. See UpdateProductHandler for the rationale.
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
