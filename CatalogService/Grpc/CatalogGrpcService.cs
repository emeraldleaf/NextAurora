using CatalogService.Features;
using Grpc.Core;
using Wolverine;

namespace CatalogService.Grpc;

/// <summary>
/// gRPC server implementation for CatalogService — exposed to other services for synchronous
/// product validation. The contract is defined in <c>Protos/catalog.proto</c>; the generated
/// <c>CatalogGrpcBase</c> is what we override here.
///
/// <para>
/// <b>Why gRPC instead of HTTP/REST for this:</b> OrderService calls into here on the
/// synchronous order-placement path, once per line item. Binary protobuf is ~5× smaller than
/// JSON for the same payload, HTTP/2 multiplexes multiple calls over one connection, and the
/// generated client has zero serialization ambiguity. None of these matter for end-user APIs
/// (browser → REST is fine), but they all matter for service-to-service hot paths.
/// </para>
/// <para>
/// <b>SOLID — same business logic, different transport:</b> notice how thin this is. Every
/// method translates a gRPC request into a Wolverine command/query and translates the result
/// back. The actual product lookup and stock reservation logic lives in the Application
/// handlers — same handlers the HTTP endpoints use. If the handlers change, gRPC and REST
/// stay consistent automatically.
/// </para>
/// <para>
/// <b>Error handling pattern:</b> gRPC has its own status codes via <see cref="RpcException"/>.
/// Validation errors → <c>InvalidArgument</c>; missing entity → <c>NotFound</c>. We never
/// leak internal state in the message — the same rule as HTTP.
/// </para>
/// </summary>
public class CatalogGrpcService(IMessageBus bus) : CatalogGrpc.CatalogGrpcBase
{
    public override async Task<ProductResponse> GetProduct(GetProductRequest request, ServerCallContext context)
    {
        // Wire format is string (proto3 best practice for IDs); validate the cast to Guid up
        // front so a typo doesn't get surfaced as a generic 500.
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid product ID format"));
        }

        var product = await bus.InvokeAsync<NextAurora.Contracts.DTOs.ProductDto?>(new GetProductByIdQuery(productId), context.CancellationToken);

        if (product is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "The requested product was not found"));
        }

        return MapToResponse(product);
    }

    /// <summary>
    /// Batch lookup — caller passes N product IDs, we return up to N products.
    /// Currently sequential (`foreach`); for large batches a parallel fan-out with bounded
    /// concurrency would be faster, but the per-request overhead of multiple DB round trips
    /// hasn't been a measured bottleneck. Profile before changing.
    /// </summary>
    public override async Task<ProductListResponse> GetProducts(GetProductsRequest request, ServerCallContext context)
    {
        var response = new ProductListResponse();

        foreach (var idString in request.ProductIds)
        {
            // Skip malformed IDs silently rather than failing the whole batch — partial results
            // are more useful for the caller than a hard failure on one bad input.
            if (!Guid.TryParse(idString, out var productId))
            {
                continue;
            }

            var product = await bus.InvokeAsync<NextAurora.Contracts.DTOs.ProductDto?>(new GetProductByIdQuery(productId), context.CancellationToken);
            if (product is not null)
            {
                response.Products.Add(MapToResponse(product));
            }
        }

        return response;
    }

    /// <summary>
    /// Reserves stock atomically. The Application handler does the work — including the
    /// optimistic-concurrency-token check that prevents two simultaneous reservations from
    /// over-allocating. From the caller's perspective, success means stock is reserved; failure
    /// means it isn't. No partial outcomes.
    /// </summary>
    public override async Task<ReserveStockResponse> ReserveStock(ReserveStockRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid product ID format"));
        }

        var success = await bus.InvokeAsync<bool>(new ReserveStockCommand(productId, request.Quantity), context.CancellationToken);
        return new ReserveStockResponse { Success = success };
    }

    // DTO → gRPC message mapping. Two notable choices:
    //   * Id and Price go over the wire as strings. Guid serialization in proto3 is via the
    //     well-known string form (avoids cross-language byte-order ambiguity). Decimals
    //     similarly: proto has no native decimal type, so string preserves precision exactly.
    //   * Culture-invariant formatting on Price ensures a server in any locale produces the
    //     same wire representation.
    private static ProductResponse MapToResponse(NextAurora.Contracts.DTOs.ProductDto product) =>
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
