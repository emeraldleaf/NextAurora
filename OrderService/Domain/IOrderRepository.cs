using NextAurora.Contracts.DTOs;

namespace OrderService.Domain;

/// <summary>
/// Order data access. Read and write paths take different methods by design — see
/// <c>docs/cqrs-data-access.md</c> for the rule and rationale.
///
/// <para>
/// <b>Write loaders</b> (<see cref="GetByIdAsync"/>) return the tracked <see cref="Order"/>
/// aggregate so command/saga handlers can mutate via aggregate methods and SaveChanges.
/// <c>Include</c>s the line collection because the mutation paths inspect or modify it.
/// </para>
/// <para>
/// <b>Read projections</b> (<see cref="GetSummaryByIdAsync"/>,
/// <see cref="GetSummariesByBuyerIdAsync"/>) return <see cref="OrderSummaryDto"/> directly
/// by projecting inside the IQueryable. No entity materialization, no in-memory mapper,
/// no parent-cartesian rows. The signature is the proof of intent: DTO-returning = read.
/// </para>
/// </summary>
public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<OrderSummaryDto?> GetSummaryByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<OrderSummaryDto>> GetSummariesByBuyerIdAsync(
        Guid buyerId, int page, int pageSize, CancellationToken ct = default);

    Task AddAsync(Order order, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
}
