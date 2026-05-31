using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Features;

/// <summary>
/// "Get all products (paginated)" vertical slice. Read-only — projects to <see cref="ProductDto"/>
/// inline via <c>AsNoTracking().Select(...)</c> with <c>OrderBy(p =&gt; p.Id)</c> for stable
/// pagination across pages. The endpoint enforces a server-side page-size cap of 100 via
/// <c>ClampPaging</c>; the defense-in-depth clamp in the handler protects future non-endpoint
/// callers.
/// </summary>
public record GetAllProductsQuery(int Page = 1, int PageSize = 50);

public class GetAllProductsHandler(CatalogDbContext context)
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(GetAllProductsQuery request, CancellationToken cancellationToken)
    {
        var safePage = request.Page < 1 ? 1 : request.Page;
        var safePageSize = request.PageSize is < 1 or > 100 ? 50 : request.PageSize;

        // Compute Skip offset in long arithmetic to avoid int overflow when a caller
        // passes a huge page (e.g. int.MaxValue). Negative offsets throw at execution.
        var skipOffset = (long)(safePage - 1) * safePageSize;
        if (skipOffset > int.MaxValue)
            return [];

        return await context.Products.AsNoTracking()
            .OrderBy(p => p.Id)
            .Skip((int)skipOffset).Take(safePageSize)
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
            .ToListAsync(cancellationToken);
    }
}
