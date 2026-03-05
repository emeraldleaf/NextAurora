using MediatR;

namespace CatalogService.Application.Commands;

public record CreateProductCommand(
    string Name,
    string Description,
    decimal Price,
    string Currency,
    Guid CategoryId,
    string SellerId,
    int StockQuantity) : IRequest<Guid>;
