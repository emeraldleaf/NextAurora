using Microsoft.EntityFrameworkCore;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/>. Selective-tracking strategy:
/// <see cref="GetByIdAsync"/> is shared with saga handlers (<c>PaymentCompletedHandler</c>,
/// <c>PaymentFailedHandler</c>, <c>ShipmentDispatchedHandler</c>) so it leaves tracking ON;
/// <see cref="GetByBuyerIdAsync"/> is read-only so it uses <c>AsNoTracking</c>.
/// </summary>
public class OrderRepository(OrderDbContext context) : IOrderRepository
{
    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    /// <summary>
    /// Buyer order history, sorted newest-first. Read-only path: <c>AsNoTracking</c> + projection
    /// to DTO downstream.
    /// </summary>
    public async Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, int page, int pageSize, CancellationToken ct = default)
        => await context.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.BuyerId == buyerId)
            .OrderByDescending(o => o.PlacedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

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
