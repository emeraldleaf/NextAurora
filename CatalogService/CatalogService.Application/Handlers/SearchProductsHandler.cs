using CatalogService.Domain.Interfaces;
using MediatR;
using NovaCraft.Contracts.DTOs;
using CatalogService.Application.Queries;

namespace CatalogService.Application.Handlers;

public class SearchProductsHandler(IProductRepository repository) : IRequestHandler<SearchProductsQuery, IReadOnlyList<ProductDto>>
{
    public async Task<IReadOnlyList<ProductDto>> Handle(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        var products = await repository.SearchAsync(request.Query, cancellationToken);
        return products.Select(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Price = p.Price,
            Currency = p.Currency,
            Category = p.Category?.Name ?? "",
            SellerId = p.SellerId,
            StockQuantity = p.StockQuantity,
            IsAvailable = p.IsAvailable
        }).ToList();
    }
}
