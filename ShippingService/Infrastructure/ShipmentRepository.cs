using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.DTOs;
using ShippingService.Domain;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IShipmentRepository"/>. Read and write paths take
/// separate methods on purpose — see <c>docs/cqrs-data-access.md</c>.
///
/// <para>
/// <b>Write loader</b> (<see cref="GetByOrderIdAsync"/>): tracking ON, <c>Include</c> on
/// <see cref="Shipment.TrackingEvents"/>. <c>CreateShipmentHandler</c> uses it to detect a
/// pre-existing shipment for the same order (idempotency under at-least-once delivery).
/// </para>
/// <para>
/// <b>Read projection</b> (<see cref="GetSummaryByOrderIdAsync"/>): <c>AsNoTracking()</c> +
/// <c>.Select(...)</c> directly to <see cref="ShipmentDto"/>. The DTO carries
/// <see cref="ShipmentDto.BuyerId"/> so <c>GetShipmentByOrderHandler</c> can enforce the
/// owner-match IDOR check on the projection without materializing the aggregate.
/// </para>
/// </summary>
public class ShipmentRepository(ShippingDbContext context) : IShipmentRepository
{
    public async Task<Shipment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await context.Shipments.Include(s => s.TrackingEvents).FirstOrDefaultAsync(s => s.OrderId == orderId, ct);

    public async Task<ShipmentDto?> GetSummaryByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await context.Shipments.AsNoTracking()
            .Where(s => s.OrderId == orderId)
            .Select(s => new ShipmentDto(
                s.Id,
                s.OrderId,
                s.BuyerId,
                s.Carrier,
                s.TrackingNumber,
                s.Status.ToString(),
                s.CreatedAt,
                s.DispatchedAt,
                s.TrackingEvents.Select(e => new TrackingEventDto(e.Description, e.Status, e.OccurredAt)).ToList()))
            .FirstOrDefaultAsync(ct);

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
