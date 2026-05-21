using CatalogService.Api.Grpc;
using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Infrastructure;

/// <summary>
/// Adapter from <see cref="ICatalogClient"/> (OrderService's domain-friendly abstraction) to the
/// generated <c>CatalogGrpcClient</c>. Feature handlers depend on <see cref="ICatalogClient"/>,
/// never on the gRPC client directly. Tests substitute a fake.
///
/// <para>
/// <b>Two important details on every call:</b>
/// </para>
/// <list type="bullet">
///   <item><b>Deadline (5s):</b> gRPC supports per-call deadlines that propagate to the server.
///         If the catalog can't answer in 5 seconds we'd rather fail the order placement than
///         hang the user-facing request.</item>
///   <item><b>Catch <c>NotFound</c>, return null:</b> on the read path, "product doesn't exist"
///         is a normal application result, not an exception. Other status codes still throw.</item>
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
            return false;
        }
    }
}
