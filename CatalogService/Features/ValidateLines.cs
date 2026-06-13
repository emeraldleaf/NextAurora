using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Features;

/// <summary>
/// "Validate order lines" vertical slice. Called via gRPC from OrderService during order
/// placement: one batch call returns availability + price for every product in the order,
/// replacing the previous per-line <c>GetProduct</c> fan-out (N round-trips → 1).
///
/// <para>
/// <b>One SQL query for the whole batch:</b> <c>WHERE p.Id IN (...)</c> with an
/// <c>AsNoTracking().Select(...)</c> projection — the read-side shape per CLAUDE.md
/// "Performance Rules → EF Core reads". Missing IDs are simply absent from the result;
/// the caller treats absence as "product not found".
/// </para>
/// <para>
/// <b>Why this bypasses <c>IProductCache</c>:</b> the cache is keyed per-product and this
/// read feeds a stock-sensitive decision (the very next step reserves the stock). A cached
/// <c>StockQuantity</c> can be seconds stale, which would let validation pass only for the
/// reservation to fail — a worse buyer experience than one fresh batch query. The
/// authoritative check is <see cref="ReserveLinesHandler"/> anyway; this read exists to
/// fail fast and to fetch server-controlled prices.
/// </para>
/// </summary>
public record ValidateLinesQuery(List<Guid> ProductIds);

public class ValidateLinesQueryValidator : AbstractValidator<ValidateLinesQuery>
{
    public ValidateLinesQueryValidator()
    {
        RuleFor(x => x.ProductIds).NotEmpty();
        RuleFor(x => x.ProductIds.Count).LessThanOrEqualTo(100);
        RuleForEach(x => x.ProductIds).NotEmpty();
    }
}

public class ValidateLinesHandler(CatalogDbContext context)
{
    public async Task<List<ProductDto>> HandleAsync(ValidateLinesQuery request, CancellationToken cancellationToken)
        => await context.Products.AsNoTracking()
            .Where(p => request.ProductIds.Contains(p.Id))
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
