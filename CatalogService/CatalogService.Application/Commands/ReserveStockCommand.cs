namespace CatalogService.Application.Commands;

public record ReserveStockCommand(Guid ProductId, int Quantity);
