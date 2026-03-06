using NextAurora.Contracts.DTOs;

namespace OrderService.Application.Interfaces;

public interface ICatalogClient
{
    Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<bool> ReserveStockAsync(Guid productId, int quantity, CancellationToken ct = default);
}
