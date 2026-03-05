using MediatR;
using NextAurora.Contracts.DTOs;
using OrderService.Application.Queries;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

public class GetOrdersByBuyerHandler(IOrderRepository repository)
    : IRequestHandler<GetOrdersByBuyerQuery, IReadOnlyList<OrderSummaryDto>>
{
    public async Task<IReadOnlyList<OrderSummaryDto>> Handle(GetOrdersByBuyerQuery request, CancellationToken cancellationToken)
    {
        var orders = await repository.GetByBuyerIdAsync(request.BuyerId, cancellationToken);
        return orders.Select(order => new OrderSummaryDto
        {
            OrderId = order.Id,
            BuyerId = order.BuyerId,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            PlacedAt = order.PlacedAt,
            Lines = order.Lines.Select(l => new OrderLineSummaryDto
            {
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        }).ToList();
    }
}
