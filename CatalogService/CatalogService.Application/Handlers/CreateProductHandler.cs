using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using MediatR;
using CatalogService.Application.Commands;

namespace CatalogService.Application.Handlers;

public class CreateProductHandler(IProductRepository repository) : IRequestHandler<CreateProductCommand, Guid>
{
    public async Task<Guid> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var product = Product.Create(
            request.Name, request.Description, request.Price,
            request.Currency, request.CategoryId, request.SellerId, request.StockQuantity);

        await repository.AddAsync(product, cancellationToken);
        return product.Id;
    }
}
