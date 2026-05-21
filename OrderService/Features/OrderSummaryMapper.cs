using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// Single source of truth for <see cref="Order"/> → <see cref="OrderSummaryDto"/> projection
/// (including nested <c>OrderLine → OrderLineSummaryDto</c>). Two query handlers
/// (<see cref="GetOrderByIdHandler"/>, <see cref="GetOrdersByBuyerHandler"/>) used to open-code
/// the same copy; centralised so that adding a field to the DTO touches one file. Status is
/// stringified at the DTO boundary — the API never returns the underlying enum integer.
/// </summary>
internal static class OrderSummaryMapper
{
    public static OrderSummaryDto ToDto(Order order) => new()
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
