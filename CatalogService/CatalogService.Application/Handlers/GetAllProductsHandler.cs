using CatalogService.Domain.Interfaces;
using MediatR;
using NovaCraft.Contracts.DTOs;
using CatalogService.Application.Queries;

namespace CatalogService.Application.Handlers;

public class GetAllProductsHandler(IProductRepository repository) : IRequestHandler<GetAllProductsQuery, IReadOnlyList<ProductDto>>
{
    public async Task<IReadOnlyList<ProductDto>> Handle(GetAllProductsQuery request, CancellationToken cancellationToken)
    {
        var products = await repository.GetAllAsync(cancellationToken);
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
