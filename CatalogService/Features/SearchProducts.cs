using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Features;

/// <summary>
/// "Search products" vertical slice. Case-insensitive substring search across name +
/// description, paginated.
///
/// <para>
/// <b>Why <c>EF.Functions.ILike</c>:</b> plain <c>.Contains</c> translates to a case-sensitive
/// <c>LIKE</c> on Postgres, so a search for "laptop" misses "Laptop". <c>ILike</c> is Postgres's
/// case-insensitive variant — translates to <c>name ILIKE @pattern</c>. The leading wildcard
/// means no B-tree index can be used either way; full-text search (<c>tsvector</c>) is the next
/// step if this becomes a bottleneck. See CLAUDE.md "Measure before optimizing".
/// </para>
/// </summary>
public record SearchProductsQuery(string Query, int Page = 1, int PageSize = 50);

public class SearchProductsHandler(CatalogDbContext context)
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        // Guard against empty/whitespace queries — without this, `$"%{request.Query}%"` becomes
        // `"%%"` which `ILike` matches against EVERY row. That turns a "no search term entered"
        // request into an unbounded full-table dump (subject to pagination, but still an
        // unintended scan). The endpoint also caps `query.Length > 200`, but doesn't reject
        // empty strings — they're a separate failure mode worth blocking explicitly.
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        var safePage = request.Page < 1 ? 1 : request.Page;
        var safePageSize = request.PageSize is < 1 or > 100 ? 50 : request.PageSize;

        var skipOffset = (long)(safePage - 1) * safePageSize;
        if (skipOffset > int.MaxValue)
            return [];

        var pattern = $"%{request.Query}%";
        return await context.Products.AsNoTracking()
            .Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Description, pattern))
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
