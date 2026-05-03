using NextAurora.Contracts.DTOs;
using OrderService.Application.Queries;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

/// <summary>
/// Canonical query handler — read-only, returns a DTO, never mutates state.
///
/// <para>
/// <b>CQRS:</b> queries return DTOs, not domain entities. Why: domain entities are mutable rich
/// objects with private setters and behavior; exposing them through the API would either leak
/// internals (forcing public setters that defeat encapsulation) or leak EF tracking artifacts.
/// DTOs are flat data shapes designed for serialization and the consumer's needs.
/// </para>
/// <para>
/// <b>The mapping looks repetitive — why not AutoMapper?</b> Two reasons: explicit mapping is
/// trivial to read and step through; reflection-based mappers obscure where each field comes
/// from. With ~6 query handlers in the system, the cost of writing the mapping by hand is
/// minimal compared to the readability win.
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
    public async Task<OrderSummaryDto?> Handle(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await repository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null) return null;

        return new OrderSummaryDto
        {
            OrderId = order.Id,
            BuyerId = order.BuyerId,
            // Enum stored as string in the DB and exposed as string in the DTO — the API never
            // returns underlying integer enum values to clients.
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            PlacedAt = order.PlacedAt,
            Lines = order.Lines.Select(l => new OrderLineSummaryDto
            {
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };
    }
}
