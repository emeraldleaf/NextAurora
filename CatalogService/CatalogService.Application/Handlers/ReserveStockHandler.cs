using CatalogService.Application.Commands;
using CatalogService.Domain.Interfaces;
using MediatR;

namespace CatalogService.Application.Handlers;

public class ReserveStockHandler(IProductRepository repository) : IRequestHandler<ReserveStockCommand, bool>
{
    public async Task<bool> Handle(ReserveStockCommand request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        if (product.StockQuantity < request.Quantity)
            return false;

        product.AdjustStock(product.StockQuantity - request.Quantity);
        await repository.UpdateAsync(product, cancellationToken);
        return true;
    }
}
