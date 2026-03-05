using CatalogService.Api.Grpc;
using NextAurora.Contracts.DTOs;
using OrderService.Application.Interfaces;

namespace OrderService.Api.GrpcClients;

public class GrpcCatalogClient(CatalogGrpc.CatalogGrpcClient client) : ICatalogClient
{
    public async Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetProductAsync(
                new GetProductRequest { ProductId = productId.ToString() },
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
}
