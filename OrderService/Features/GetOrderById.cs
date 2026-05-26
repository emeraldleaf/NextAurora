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
///
/// <para>
/// <b>IDOR protection (CLAUDE.md "Security Requirements").</b> The buyer-ownership predicate
/// lives in the EF <c>Where</c> clause itself — both <c>o.Id == OrderId</c> AND
/// <c>o.BuyerId == RequestingBuyerId</c> are SQL predicates. A non-owner request returns
/// <c>null</c> straight from the database without the row ever crossing the wire;
/// indistinguishable from "order doesn't exist." The endpoint translates <c>null</c> to 404
/// (NOT 403) per the canonical anti-enumeration pattern — same shape as
/// <c>ShippingService.Features.GetShipmentByOrderHandler</c>. The endpoint extracts the
/// requesting buyer's ID from the JWT <c>NameIdentifier</c> claim and passes it in — the
/// caller cannot supply <c>RequestingBuyerId</c> via URL or body.
/// </para>
/// </summary>
public record GetOrderByIdQuery(Guid OrderId, Guid RequestingBuyerId);

public class GetOrderByIdHandler(OrderDbContext context)
{
    public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
        => context.Orders.AsNoTracking()
            .Where(o => o.Id == request.OrderId && o.BuyerId == request.RequestingBuyerId)
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
