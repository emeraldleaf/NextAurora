using MediatR;
using NovaCraft.Contracts.DTOs;
using OrderService.Application.Queries;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

public class GetOrderByIdHandler(IOrderRepository repository) : IRequestHandler<GetOrderByIdQuery, OrderSummaryDto?>
{
    public async Task<OrderSummaryDto?> Handle(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null) return null;

        return new OrderSummaryDto
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
        };
    }
}
