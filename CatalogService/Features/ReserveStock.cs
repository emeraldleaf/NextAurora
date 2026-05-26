using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Features;

/// <summary>
/// "Reserve stock" vertical slice. Called via gRPC from OrderService during order placement.
/// Mutates <c>StockQuantity</c> (and transitively <c>IsAvailable</c>) on a Product aggregate.
///
/// <para>
/// <b>Concurrency story:</b> the actual race protection is the Postgres <c>xmin</c> concurrency
/// token on Product (configured in <c>CatalogDbContext</c>). Two simultaneous reservations can't
/// both win; the loser gets <see cref="DbUpdateConcurrencyException"/> and returns false. The
/// cache invalidation is downstream — runs after a successful save, so the cache can't outlive
/// the concurrency check.
/// </para>
/// </summary>
public record ReserveStockCommand(Guid ProductId, int Quantity);

public class ReserveStockCommandValidator : AbstractValidator<ReserveStockCommand>
{
    public ReserveStockCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
    }
}

public class ReserveStockHandler(CatalogDbContext context, IProductCache cache)
{
    public async Task<bool> HandleAsync(ReserveStockCommand request, CancellationToken cancellationToken)
    {
        var product = await context.Products.FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (product is null)
            return false;

        if (product.StockQuantity < request.Quantity)
            return false;

        product.AdjustStock(product.StockQuantity - request.Quantity);
        await context.SaveChangesAsync(cancellationToken);

        // Invalidate AFTER the save. See UpdateProduct for the cache-ordering rationale.
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
