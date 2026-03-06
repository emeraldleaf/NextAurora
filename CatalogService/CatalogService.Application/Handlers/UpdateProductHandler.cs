using CatalogService.Domain.Interfaces;
using CatalogService.Application.Commands;

namespace CatalogService.Application.Handlers;

public class UpdateProductHandler(IProductRepository repository)
{
    public async Task Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {request.ProductId} not found");

        product.UpdateDetails(request.Name, request.Description, request.Price);
        await repository.UpdateAsync(product, cancellationToken);
    }
}
