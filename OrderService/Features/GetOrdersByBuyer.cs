using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Features;

/// <summary>
/// "Get orders by buyer" vertical slice. Paginated, read-only. Projects to
/// <see cref="OrderSummaryDto"/> inline via <c>AsNoTracking() + .Select(...)</c>. No
/// repository wrapper. Defense-in-depth pagination clamp protects future non-endpoint
/// callers (the endpoint's <c>ClampPaging</c> is the primary cap).
/// </summary>
public record GetOrdersByBuyerQuery(Guid BuyerId, int Page = 1, int PageSize = 50);

public class GetOrdersByBuyerHandler(OrderDbContext context)
{
    public async Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(GetOrdersByBuyerQuery request, CancellationToken cancellationToken)
    {
        var safePage = request.Page < 1 ? 1 : request.Page;
        var safePageSize = request.PageSize is < 1 or > 100 ? 50 : request.PageSize;

        // Compute Skip offset in long arithmetic to avoid int overflow when a caller
        // passes a huge page (e.g. int.MaxValue). Negative offsets throw at execution.
        var skipOffset = (long)(safePage - 1) * safePageSize;
        if (skipOffset > int.MaxValue)
            return [];

        return await context.Orders.AsNoTracking()
            .Where(o => o.BuyerId == request.BuyerId)
            .OrderByDescending(o => o.PlacedAt)
            .Skip((int)skipOffset).Take(safePageSize)
            .Select(o => new OrderSummaryDto
            {
                OrderId = o.Id,
                BuyerId = o.BuyerId,
                Status = o.Status.ToString(),
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                PlacedAt = o.PlacedAt,
                Lines = o.Lines.Select(l => new OrderLineSummaryDto
                {
                    ProductId = l.ProductId,
                    ProductName = l.ProductName,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice
                }).ToList()
            })
            .ToListAsync(cancellationToken);
    }
}
