using NovaCraft.Contracts.DTOs;

namespace OrderService.Application.Interfaces;

public interface ICatalogClient
{
    Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default);
}
