using NextAurora.Contracts.DTOs;

namespace OrderService.Domain;

/// <summary>
/// Port for talking to CatalogService. <see cref="Features.PlaceOrderHandler"/> uses it to
/// validate products and reserve stock during order placement. The Infrastructure layer's
/// <c>GrpcCatalogClient</c> implements this against the generated gRPC client; tests substitute
/// a fake.
/// </summary>
public interface ICatalogClient
{
    Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<bool> ReserveStockAsync(Guid productId, int quantity, CancellationToken ct = default);
}
