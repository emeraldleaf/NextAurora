using CatalogService.Domain.Interfaces;
using NextAurora.Contracts.DTOs;
using CatalogService.Application.Queries;

namespace CatalogService.Application.Handlers;

public class GetProductByIdHandler(IProductRepository repository)
{
    public async Task<ProductDto?> Handle(GetProductByIdQuery request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null) return null;

        return new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Currency = product.Currency,
            Category = product.Category?.Name ?? "",
            SellerId = product.SellerId,
            StockQuantity = product.StockQuantity,
            IsAvailable = product.IsAvailable
        };
    }
}
