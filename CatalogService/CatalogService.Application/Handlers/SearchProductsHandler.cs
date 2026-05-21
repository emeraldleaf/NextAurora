using CatalogService.Application.Mappers;
using CatalogService.Application.Queries;
using CatalogService.Domain.Interfaces;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

public class SearchProductsHandler(IProductRepository repository)
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        var products = await repository.SearchAsync(request.Query, request.Page, request.PageSize, cancellationToken);
        return products.Select(ProductMapper.ToDto).ToList();
    }
}
