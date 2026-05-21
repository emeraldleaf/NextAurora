using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// "Get orders by buyer" vertical slice. Paginated, read-only. Reuses
/// <see cref="OrderSummaryMapper"/> for the projection.
/// </summary>
public record GetOrdersByBuyerQuery(Guid BuyerId, int Page = 1, int PageSize = 50);

public class GetOrdersByBuyerHandler(IOrderRepository repository)
{
    public async Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(GetOrdersByBuyerQuery request, CancellationToken cancellationToken)
    {
        var orders = await repository.GetByBuyerIdAsync(request.BuyerId, request.Page, request.PageSize, cancellationToken);
        return orders.Select(OrderSummaryMapper.ToDto).ToList();
    }
}
