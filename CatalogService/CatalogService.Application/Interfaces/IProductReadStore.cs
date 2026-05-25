using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Interfaces;

/// <summary>
/// Read-side data access for the Product aggregate. Lives in <see cref="Application"/> (not
/// Domain) because the interface returns <see cref="ProductDto"/> — a Contracts concern — and
/// Domain cannot reference Contracts under the project's Clean Architecture layer rules.
///
/// <para>
/// <b>Why a separate interface from <c>IProductRepository</c>:</b> read and write paths take
/// different method shapes by design (see <c>docs/cqrs-data-access.md</c>). Read methods
/// project to DTOs in EF (<c>AsNoTracking().Select(...)</c>) so the entity never materializes
/// on the read path — no parent-cartesian rows from collection includes, no in-memory
/// mapper, no over-read of write-only columns like RowVersion. Write methods (on
/// <c>IProductRepository</c> in the Domain layer) return tracked entities for mutation.
/// </para>
/// <para>
/// <b>Implementation</b> lives in <c>CatalogService.Infrastructure.Repositories.ProductReadStore</c>.
/// <b>Consumers:</b> <c>GetProductByIdHandler</c> (cached via <see cref="IProductCache"/>),
/// <c>GetAllProductsHandler</c>, <c>SearchProductsHandler</c>.
/// </para>
/// </summary>
public interface IProductReadStore
{
    Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ProductDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default);
}
