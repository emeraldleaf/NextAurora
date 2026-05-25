using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// "Get orders by buyer" vertical slice. Paginated, read-only. Projection happens inside
/// <see cref="IOrderRepository.GetSummariesByBuyerIdAsync"/> via <c>AsNoTracking() + Select</c>,
/// so this handler is a one-liner passthrough — no entity, no in-memory mapper. See
/// <c>docs/cqrs-data-access.md</c> for the read/write split rule.
/// </summary>
public record GetOrdersByBuyerQuery(Guid BuyerId, int Page = 1, int PageSize = 50);

public class GetOrdersByBuyerHandler(IOrderRepository repository)
{
    public Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(GetOrdersByBuyerQuery request, CancellationToken cancellationToken)
        => repository.GetSummariesByBuyerIdAsync(request.BuyerId, request.Page, request.PageSize, cancellationToken);
}
