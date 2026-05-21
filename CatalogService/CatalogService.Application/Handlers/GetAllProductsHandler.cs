using CatalogService.Application.Mappers;
using CatalogService.Application.Queries;
using CatalogService.Domain.Interfaces;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Handlers;

public class GetAllProductsHandler(IProductRepository repository)
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(GetAllProductsQuery request, CancellationToken cancellationToken)
    {
        var products = await repository.GetAllAsync(request.Page, request.PageSize, cancellationToken);
        return products.Select(ProductMapper.ToDto).ToList();
    }
}
