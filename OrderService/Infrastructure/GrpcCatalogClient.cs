using CatalogService.Grpc;
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
///   <item><b>Batch semantics:</b> both methods are one round-trip for the whole order.
///         <c>ValidateLines</c> omits unknown products from the response (absence = not found,
///         a normal application result). <c>ReserveLines</c> is atomic on the server — any
///         RpcException maps to <c>false</c> ("nothing was reserved").</item>
/// </list>
/// </summary>
public class GrpcCatalogClient(CatalogGrpc.CatalogGrpcClient client) : ICatalogClient
{
    public async Task<IReadOnlyList<ProductDto>> ValidateLinesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var request = new ValidateLinesRequest();
        foreach (var id in productIds)
        {
            request.ProductIds.Add(id.ToString());
        }

        var response = await client.ValidateLinesAsync(
            request,
            deadline: DateTime.UtcNow.AddSeconds(5),
            cancellationToken: ct);

        return response.Products.Select(p => new ProductDto
        {
            Id = Guid.Parse(p.Id),
            Name = p.Name,
            Description = p.Description,
            Price = decimal.Parse(p.Price, System.Globalization.CultureInfo.InvariantCulture),
            Currency = p.Currency,
            Category = p.Category,
            SellerId = p.SellerId,
            StockQuantity = p.StockQuantity,
            IsAvailable = p.IsAvailable,
        }).ToList();
    }

    public async Task<bool> ReserveLinesAsync(IReadOnlyCollection<CatalogReserveLine> lines, CancellationToken ct = default)
    {
        var request = new ReserveLinesRequest();
        foreach (var line in lines)
        {
            request.Lines.Add(new ReserveLineItem { ProductId = line.ProductId.ToString(), Quantity = line.Quantity });
        }

        try
        {
            var response = await client.ReserveLinesAsync(
                request,
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
