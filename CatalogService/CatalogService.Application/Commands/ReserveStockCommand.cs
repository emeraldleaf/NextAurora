using MediatR;

namespace CatalogService.Application.Commands;

public record ReserveStockCommand(Guid ProductId, int Quantity) : IRequest<bool>;
