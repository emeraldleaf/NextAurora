using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/>. Read and write paths take
/// separate methods on purpose — see <c>docs/cqrs-data-access.md</c>.
///
/// <para>
/// <b>Write loader</b> (<see cref="GetByIdAsync"/>): tracking ON, <c>Include</c> on
/// <see cref="Order.Lines"/> because saga handlers (<c>PaymentCompletedHandler</c>,
/// <c>PaymentFailedHandler</c>, <c>ShipmentDispatchedHandler</c>) mutate the aggregate
/// and call <c>SaveChanges</c>.
/// </para>
/// <para>
/// <b>Read projections</b> (<see cref="GetSummaryByIdAsync"/>,
/// <see cref="GetSummariesByBuyerIdAsync"/>): <c>AsNoTracking()</c> + <c>.Select(...)</c>
/// inside the IQueryable. EF emits SQL that selects only the DTO columns, projects child
/// collections via a correlated subquery, and never materializes an <see cref="Order"/>
/// entity. No in-memory mapper hop, no <c>RowVersion</c> over the wire, no parent-cartesian
/// duplication from a collection <c>Include</c>.
/// </para>
/// </summary>
public class OrderRepository(OrderDbContext context) : IOrderRepository
{
    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<OrderSummaryDto?> GetSummaryByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Orders.AsNoTracking()
            .Where(o => o.Id == id)
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
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<OrderSummaryDto>> GetSummariesByBuyerIdAsync(
        Guid buyerId, int page, int pageSize, CancellationToken ct = default)
    {
        // Defense-in-depth pagination clamp. The HTTP endpoint's ClampPaging already
        // bounds (page >= 1, pageSize 1..100), so under normal call flow this branch
        // is a no-op. The clamp here protects against future non-endpoint callers
        // (admin tools, background jobs, gRPC handlers) that bypass the endpoint and
        // could otherwise issue an oversized query (pageSize=10000 = full-table scan)
        // or a malformed one (page=0 → negative offset → EF throws at execution time).
        // CLAUDE.md "Performance Rules" caps list endpoints at 100; this enforces the
        // same cap at the repository so the cap holds regardless of entry point.
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize is < 1 or > 100 ? 50 : pageSize;

        // Compute Skip's offset in long arithmetic to avoid int overflow when a caller
        // passes a huge page (e.g. int.MaxValue). (safePage - 1) * safePageSize in int
        // arithmetic would wrap to a negative number, and Skip(-N) throws at execution.
        // If the offset exceeds the addressable result set, return empty rather than
        // letting EF translate a too-large OFFSET into a slow no-result query.
        var skipOffset = (long)(safePage - 1) * safePageSize;
        if (skipOffset > int.MaxValue)
            return Array.Empty<OrderSummaryDto>();

        return await context.Orders.AsNoTracking()
            .Where(o => o.BuyerId == buyerId)
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
            .ToListAsync(ct);
    }

    public async Task AddAsync(Order order, CancellationToken ct = default)
    {
        await context.Orders.AddAsync(order, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        context.Orders.Update(order);
        await context.SaveChangesAsync(ct);
    }
}
