using CatalogService.Api.Grpc;
using CatalogService.Application.Queries;
using Grpc.Core;
using MediatR;

namespace CatalogService.Api.Services;

public class CatalogGrpcService(ISender sender) : CatalogGrpc.CatalogGrpcBase
{
    public override async Task<ProductResponse> GetProduct(GetProductRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid product ID format"));
        }

        var product = await sender.Send(new GetProductByIdQuery(productId), context.CancellationToken);

        if (product is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Product {request.ProductId} not found"));
        }

        return MapToResponse(product);
    }

    public override async Task<ProductListResponse> GetProducts(GetProductsRequest request, ServerCallContext context)
    {
        var response = new ProductListResponse();

        foreach (var idString in request.ProductIds)
        {
            if (!Guid.TryParse(idString, out var productId))
            {
                continue;
            }

            var product = await sender.Send(new GetProductByIdQuery(productId), context.CancellationToken);
            if (product is not null)
            {
                response.Products.Add(MapToResponse(product));
            }
        }

        return response;
    }

    private static ProductResponse MapToResponse(NovaCraft.Contracts.DTOs.ProductDto product) =>
        new()
        {
            Id = product.Id.ToString(),
            Name = product.Name,
            Description = product.Description,
            Price = product.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Currency = product.Currency,
            Category = product.Category,
            SellerId = product.SellerId,
            StockQuantity = product.StockQuantity,
            IsAvailable = product.IsAvailable,
        };
}
