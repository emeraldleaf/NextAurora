using NextAurora.Contracts.DTOs;
using OrderService.Application.Mappers;
using OrderService.Application.Queries;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

public class GetOrdersByBuyerHandler(IOrderRepository repository)
{
    public async Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(GetOrdersByBuyerQuery request, CancellationToken cancellationToken)
    {
        var orders = await repository.GetByBuyerIdAsync(request.BuyerId, request.Page, request.PageSize, cancellationToken);
        return orders.Select(OrderSummaryMapper.ToDto).ToList();
    }
}
