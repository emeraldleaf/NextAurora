using NextAurora.Contracts.DTOs;

namespace ShippingService.Domain;

/// <summary>
/// Shipment data access. Read and write paths take different methods by design — see
/// <c>docs/cqrs-data-access.md</c> for the rule and rationale.
///
/// <para>
/// <b>Write loader</b> (<see cref="GetByOrderIdAsync"/>) returns the tracked
/// <see cref="Shipment"/> aggregate. <c>CreateShipmentHandler</c> uses it as an
/// idempotency check (no shipment yet → create a new one; existing one → no-op).
/// </para>
/// <para>
/// <b>Read projection</b> (<see cref="GetSummaryByOrderIdAsync"/>) returns
/// <see cref="ShipmentDto"/> by projecting in EF. <c>GetShipmentByOrderHandler</c> applies
/// the ownership check on the DTO; no entity ever materializes on the read path.
/// </para>
/// </summary>
public interface IShipmentRepository
{
    Task<Shipment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);

    Task<ShipmentDto?> GetSummaryByOrderIdAsync(Guid orderId, CancellationToken ct = default);

    Task AddAsync(Shipment shipment, CancellationToken ct = default);
    Task UpdateAsync(Shipment shipment, CancellationToken ct = default);
}
