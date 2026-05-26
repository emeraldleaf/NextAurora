using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Features;

/// <summary>
/// "Get order by ID" vertical slice. Read-only — projects to <see cref="OrderSummaryDto"/>
/// inline via <c>AsNoTracking() + .Select(...)</c>. No repository wrapper, no in-memory mapper.
/// EF auto-splits the projected <c>Lines</c> collection so there are no parent-cartesian rows
/// on the wire (see <c>docs/cqrs-data-access.md</c>).
/// </summary>
public record GetOrderByIdQuery(Guid OrderId);

public class GetOrderByIdHandler(OrderDbContext context)
{
    public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
        => context.Orders.AsNoTracking()
            .Where(o => o.Id == request.OrderId)
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
            .FirstOrDefaultAsync(cancellationToken);
}
