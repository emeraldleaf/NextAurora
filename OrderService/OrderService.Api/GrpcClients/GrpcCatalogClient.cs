using CatalogService.Api.Grpc;
using NextAurora.Contracts.DTOs;
using OrderService.Application.Interfaces;

namespace OrderService.Api.GrpcClients;

/// <summary>
/// Adapter from <see cref="ICatalogClient"/> (OrderService's domain-friendly abstraction) to the
/// generated <c>CatalogGrpcClient</c>. Application-layer handlers depend on
/// <see cref="ICatalogClient"/>, never on the gRPC client directly — that's the Dependency
/// Inversion principle in practice. Replace gRPC with HTTP/JSON, an in-memory stub for testing,
/// or a different transport entirely, and the handlers don't change.
///
/// <para>
/// <b>Two important details on every call:</b>
/// </para>
/// <list type="bullet">
///   <item><b>Deadline (5s):</b> gRPC supports per-call deadlines that propagate to the server.
///         If the catalog can't answer in 5 seconds we'd rather fail the order placement than
///         hang the user-facing request. <see cref="CancellationToken"/> respects both this
///         deadline and the inbound request cancellation.</item>
///   <item><b>Catch <c>NotFound</c>, return null:</b> on the read path, "product doesn't exist"
///         is a normal application result, not an exception. Translating the gRPC status into
///         a null return makes the handler's null-check natural. Other status codes still
///         throw — they're real errors.</item>
/// </list>
/// </summary>
public class GrpcCatalogClient(CatalogGrpc.CatalogGrpcClient client) : ICatalogClient
{
    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetProductAsync(
                new GetProductRequest { ProductId = productId.ToString() },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: ct);

            // Wire types → DTO. The gRPC contract uses strings for Guid and decimal (see
            // CatalogGrpcService for why); we parse back here on the boundary so domain code
            // sees real types.
            return new ProductDto
            {
                Id = Guid.Parse(response.Id),
                Name = response.Name,
                Description = response.Description,
                Price = decimal.Parse(response.Price, System.Globalization.CultureInfo.InvariantCulture),
                Currency = response.Currency,
                Category = response.Category,
                SellerId = response.SellerId,
                StockQuantity = response.StockQuantity,
                IsAvailable = response.IsAvailable,
            };
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ReserveStockAsync(Guid productId, int quantity, CancellationToken ct = default)
    {
        try
        {
            var response = await client.ReserveStockAsync(
                new ReserveStockRequest { ProductId = productId.ToString(), Quantity = quantity },
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: ct);
            return response.Success;
        }
        catch (Grpc.Core.RpcException)
        {
            // Any reservation failure (gateway down, deadline exceeded, concurrency conflict)
            // returns false — the order placement handler treats it as "couldn't reserve".
            // Consider tightening this in the future to distinguish transient vs business
            // failures, but for now the simpler rule is right: if we can't confirm reservation,
            // don't proceed.
            return false;
        }
    }
}
