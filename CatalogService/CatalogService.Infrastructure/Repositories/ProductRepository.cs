using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProductRepository"/>. Sits in the Infrastructure layer;
/// the Domain layer only sees the interface, which is how Domain stays free of EF dependencies
/// (Dependency Inversion).
///
/// <para>
/// <b>Tracking strategy — selective <c>AsNoTracking</c>:</b> EF tracks every entity it loads by
/// default, building an in-memory map so it can detect changes for <c>SaveChanges</c>.
/// On read-only paths, that's pure overhead. We turn it off by calling <c>AsNoTracking()</c>.
/// On write paths, we leave it on so the entity we mutate gets persisted automatically.
/// </para>
/// <para>
/// Methods that are <b>only ever called by query handlers</b> (<c>GetAllAsync</c>,
/// <c>SearchAsync</c>, <c>GetByCategoryAsync</c>) use <c>AsNoTracking</c>. Methods called by
/// command handlers — most importantly <see cref="GetByIdAsync"/> here, used by both query AND
/// command handlers — keep tracking on. Without tracking, mutating the loaded entity and
/// calling <c>SaveChanges</c> would be a silent no-op. See <c>docs/cqrs-data-access.md</c>.
/// </para>
/// <para>
/// <b>Pagination:</b> list methods take <c>page</c>/<c>pageSize</c> and apply
/// <c>OrderBy().Skip().Take()</c>. The <c>OrderBy</c> isn't optional — without a stable order,
/// page 2 might overlap or skip rows from page 1 because SQL doesn't promise insertion order.
/// </para>
/// </summary>
public class ProductRepository(CatalogDbContext context) : IProductRepository
{
    /// <summary>
    /// Loads a Product by ID. Tracking is left ON because both update commands
    /// (<c>UpdateProductHandler</c>, <c>ReserveStockHandler</c>) and the query handler
    /// (<c>GetProductByIdHandler</c>, which projects to a DTO immediately) use this.
    /// Splitting into separate read/write repos is a future cleanup — see cqrs-data-access.md.
    /// </summary>
    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Paginated catalog listing, projected through to the query handler which then maps to a DTO.
    /// </summary>
    public async Task<IReadOnlyList<Product>> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        => await context.Products.AsNoTracking().Include(p => p.Category)
            .OrderBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Product>> GetByCategoryAsync(Guid categoryId, CancellationToken ct = default)
        => await context.Products.AsNoTracking().Include(p => p.Category).Where(p => p.CategoryId == categoryId).ToListAsync(ct);

    /// <summary>
    /// Substring search on Name or Description. <c>Contains</c> on a string column translates to
    /// SQL <c>LIKE '%query%'</c> which can't use a B-tree index — fine at our current scale, but
    /// migrating to Postgres full-text search (<c>tsvector</c>) is the next step if this becomes
    /// a bottleneck.
    /// </summary>
    public async Task<IReadOnlyList<Product>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default)
        => await context.Products.AsNoTracking().Include(p => p.Category)
            .Where(p => p.Name.Contains(query) || p.Description.Contains(query))
            .OrderBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

    public async Task AddAsync(Product product, CancellationToken ct = default)
    {
        await context.Products.AddAsync(product, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        // No need to call Update() explicitly when the entity was loaded with tracking — EF
        // detects the changes automatically. Update() here is harmless but defensive: if a
        // future refactor accidentally calls AsNoTracking on the read path, this still saves.
        context.Products.Update(product);
        await context.SaveChangesAsync(ct);
    }
}
