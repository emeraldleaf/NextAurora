using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/>. Same selective-tracking strategy
/// as <c>ProductRepository</c>: <see cref="GetByIdAsync"/> is shared with command/event
/// handlers (<c>PaymentCompletedHandler</c>, <c>PaymentFailedHandler</c>,
/// <c>ShipmentDispatchedHandler</c>) so it leaves tracking ON; <see cref="GetByBuyerIdAsync"/>
/// is read-only so it uses <c>AsNoTracking</c>.
/// </summary>
public class OrderRepository(OrderDbContext context) : IOrderRepository
{
    /// <summary>
    /// Loads one Order with its lines included. Tracking enabled — saga event handlers fetch
    /// the order, mutate state via domain methods, and call <see cref="UpdateAsync"/>.
    /// </summary>
    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    /// <summary>
    /// Buyer order history, sorted newest-first (most useful default for UIs that show "your
    /// recent orders"). Read-only path: <c>AsNoTracking</c> + projection to DTO downstream.
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
