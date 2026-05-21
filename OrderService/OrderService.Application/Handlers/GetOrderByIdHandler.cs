using NextAurora.Contracts.DTOs;
using OrderService.Application.Mappers;
using OrderService.Application.Queries;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

/// <summary>
/// Canonical query handler — read-only, returns a DTO, never mutates state.
///
/// <para>
/// <b>CQRS:</b> queries return DTOs, not domain entities. Domain entities are mutable rich
/// objects with private setters and behavior; exposing them through the API would either leak
/// internals (forcing public setters that defeat encapsulation) or leak EF tracking artifacts.
/// DTOs are flat data shapes designed for serialization and the consumer's needs.
/// </para>
/// <para>
/// <b>Mapping lives in <c>OrderSummaryMapper</c></b> so both this handler and
/// <c>GetOrdersByBuyerHandler</c> share one place to evolve the projection. AutoMapper is
/// avoided on purpose: explicit mapping is trivial to read and step through.
/// </para>
/// <para>
/// <b>Performance note:</b> we currently load the full <see cref="Domain.Entities.Order"/>
/// entity (with tracking — see repository) and map in memory. The faster pattern would be to
/// project directly to the DTO in EF (`.Select(o => new OrderSummaryDto { ... })`) which
/// generates SQL with only the columns we need and skips entity materialization. We keep the
/// current shape because <c>GetByIdAsync</c> is shared with command handlers — splitting into
/// separate read/write repos is a pending cleanup. See <c>docs/cqrs-data-access.md</c>.
/// </para>
/// </summary>
public class GetOrderByIdHandler(IOrderRepository repository)
{
    public async Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(request.OrderId, cancellationToken);
        return order is null ? null : OrderSummaryMapper.ToDto(order);
    }
}
