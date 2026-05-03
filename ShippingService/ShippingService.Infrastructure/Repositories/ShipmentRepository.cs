using Microsoft.EntityFrameworkCore;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IShipmentRepository"/>. <see cref="GetByOrderIdAsync"/>
/// is shared with the command path (<c>CreateShipmentHandler</c> uses it as an idempotency
/// check) so tracking stays ON. Both lookups <c>Include</c> the tracking events so callers
/// always see the full audit trail.
/// </summary>
public class ShipmentRepository(ShippingDbContext context) : IShipmentRepository
{
    public async Task<Shipment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Shipments.Include(s => s.TrackingEvents).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Shipment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await context.Shipments.Include(s => s.TrackingEvents).FirstOrDefaultAsync(s => s.OrderId == orderId, ct);

    public async Task AddAsync(Shipment shipment, CancellationToken ct = default)
    {
        await context.Shipments.AddAsync(shipment, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Shipment shipment, CancellationToken ct = default)
    {
        context.Shipments.Update(shipment);
        await context.SaveChangesAsync(ct);
    }
}
