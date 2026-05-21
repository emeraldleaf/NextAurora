using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// "Get order by ID" vertical slice. Read-only — uses the shared <see cref="OrderSummaryMapper"/>
/// since the projection is identical to <see cref="GetOrdersByBuyerHandler"/>.
///
/// <para>
/// <b>CQRS:</b> queries return DTOs, not domain entities. Domain entities are mutable rich
/// objects with private setters; exposing them through the API would either leak internals
/// (forcing public setters) or leak EF tracking artifacts.
/// </para>
/// <para>
/// <b>Performance note:</b> we load the full Order entity (with tracking) and map in memory.
/// The faster pattern would be to project directly to the DTO in EF (`.Select(o => new OrderSummaryDto { ... })`)
/// which generates SQL with only the columns we need and skips entity materialization. We keep
/// the current shape because <c>GetByIdAsync</c> is shared with saga handlers — splitting into
/// separate read/write repos is a pending cleanup. See <c>docs/cqrs-data-access.md</c>.
/// </para>
/// </summary>
public record GetOrderByIdQuery(Guid OrderId);

public class GetOrderByIdHandler(IOrderRepository repository)
{
    public async Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(request.OrderId, cancellationToken);
        return order is null ? null : OrderSummaryMapper.ToDto(order);
    }
}
