using CatalogService.Application.Interfaces;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProductReadStore"/>. Read paths only — projects to
/// <see cref="ProductDto"/> inside the IQueryable via <c>AsNoTracking().Select(...)</c>.
///
/// <para>
/// <b>What the SQL looks like:</b> SELECT only the DTO columns (no <c>RowVersion</c>, no audit
/// columns the DTO drops). The <see cref="ProductDto.Category"/> string projection inlines a
/// LEFT JOIN to <c>Categories</c> and reads <c>Categories.Name</c>; no entity hop, no
/// <c>AsNoTrackingWithIdentityResolution</c> needed because we never materialize a Category
/// entity — just the scalar string.
/// </para>
/// <para>
/// <b>Why a separate store, not a method on <c>IProductRepository</c>:</b> <c>IProductRepository</c>
/// is in the Domain layer, which under this service's Clean Architecture layer rules cannot
/// reference <c>NextAurora.Contracts</c> (where DTOs live). The read-side interface therefore
/// lives in Application; the write-side (entity-returning) interface stays in Domain. See
/// <c>docs/cqrs-data-access.md</c>.
/// </para>
/// </summary>
public class ProductReadStore(CatalogDbContext context) : IProductReadStore
{
    public async Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Products.AsNoTracking()
            .Where(p => p.Id == id)
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
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ProductDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        => await context.Products.AsNoTracking()
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
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
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default)
        => await context.Products.AsNoTracking()
            .Where(p => p.Name.Contains(query) || p.Description.Contains(query))
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
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
            .ToListAsync(ct);
}
