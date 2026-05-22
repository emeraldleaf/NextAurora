namespace CatalogService.Application.Commands;

public record UpdateProductCommand(
    Guid ProductId,
    string SellerId,
    string Name,
    string Description,
    decimal Price);
