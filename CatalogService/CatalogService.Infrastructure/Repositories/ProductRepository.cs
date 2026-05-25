using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Repositories;

/// <summary>
/// Write-side EF Core implementation of <see cref="IProductRepository"/>. Loads the
/// <see cref="Product"/> aggregate tracked so command handlers can mutate via aggregate
/// methods (e.g. <c>UpdateDetails</c>, <c>ReserveStock</c>) and persist with
/// <c>SaveChanges</c>. <c>Include</c>s the Category navigation because the mutation paths
/// (and the cache-warming projection in <c>IProductReadStore</c>) both need the category name.
///
/// <para>
/// Read paths use <c>CatalogService.Application.Interfaces.IProductReadStore</c> — that
/// interface returns <see cref="NextAurora.Contracts.DTOs.ProductDto"/> by projecting in EF.
/// Splitting the read and write paths into separate interfaces is the project's CQRS data-
/// access rule (see <c>docs/cqrs-data-access.md</c>); it lets the read path skip entity
/// materialization entirely while the write path keeps the tracked aggregate it needs.
/// </para>
/// </summary>
public class ProductRepository(CatalogDbContext context) : IProductRepository
{
    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(Product product, CancellationToken ct = default)
    {
        await context.Products.AddAsync(product, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Product product, CancellationToken ct = default)
    {
        context.Products.Update(product);
        await context.SaveChangesAsync(ct);
    }
}
