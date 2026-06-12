using NextAurora.Contracts.DTOs;

namespace OrderService.Domain;

/// <summary>
/// Port for talking to CatalogService. <see cref="Features.PlaceOrderHandler"/> uses it to
/// validate products and reserve stock during order placement — one batch call per phase,
/// regardless of order size. The Infrastructure layer's <c>GrpcCatalogClient</c> implements
/// this against the generated gRPC client; tests substitute a fake.
/// </summary>
public interface ICatalogClient
{
    /// <summary>
    /// Returns availability + server-controlled prices for the requested products in one
    /// round-trip. Missing IDs are absent from the result — the caller treats absence as
    /// "product not found".
    /// </summary>
    Task<IReadOnlyList<ProductDto>> ValidateLinesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Reserves stock for every line atomically: all lines reserve in one Catalog-side
    /// transaction or none do. <c>false</c> means nothing was reserved — there is no partial
    /// state to compensate.
    /// </summary>
    Task<bool> ReserveLinesAsync(IReadOnlyCollection<CatalogReserveLine> lines, CancellationToken ct = default);
}

public record CatalogReserveLine(Guid ProductId, int Quantity);
