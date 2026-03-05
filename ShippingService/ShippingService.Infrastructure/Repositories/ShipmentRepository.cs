using Microsoft.EntityFrameworkCore;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Infrastructure.Repositories;

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
