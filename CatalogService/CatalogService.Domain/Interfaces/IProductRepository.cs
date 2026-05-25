using CatalogService.Domain.Entities;

namespace CatalogService.Domain.Interfaces;

/// <summary>
/// Write-side data access for the <see cref="Product"/> aggregate. <see cref="GetByIdAsync"/>
/// returns the tracked entity so command handlers (<c>UpdateProductHandler</c>,
/// <c>ReserveStockHandler</c>) can mutate it via aggregate methods and persist with
/// <c>SaveChanges</c>. Read paths use <c>IProductReadStore</c> in the Application layer —
/// see <c>docs/cqrs-data-access.md</c> for the read/write split rule.
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
}
