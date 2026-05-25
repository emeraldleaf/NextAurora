using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

/// <summary>
/// "Get all products (paginated)" query handler. One-liner passthrough to
/// <see cref="IProductReadStore.GetAllAsync"/>, which projects in EF — no entity hop. See
/// <c>docs/cqrs-data-access.md</c> for the read/write split rule.
/// </summary>
public class GetAllProductsHandler(IProductReadStore readStore)
{
    public Task<IReadOnlyList<ProductDto>> HandleAsync(GetAllProductsQuery request, CancellationToken cancellationToken)
        => readStore.GetAllAsync(request.Page, request.PageSize, cancellationToken);
}
