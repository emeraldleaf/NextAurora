using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

/// <summary>
/// "Search products" query handler. One-liner passthrough to
/// <see cref="IProductReadStore.SearchAsync"/>, which projects in EF — no entity hop. See
/// <c>docs/cqrs-data-access.md</c> for the read/write split rule.
/// </summary>
public class SearchProductsHandler(IProductReadStore readStore)
{
    public Task<IReadOnlyList<ProductDto>> HandleAsync(SearchProductsQuery request, CancellationToken cancellationToken)
        => readStore.SearchAsync(request.Query, request.Page, request.PageSize, cancellationToken);
}
