using NextAurora.Contracts.DTOs;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// "Get order by ID" vertical slice. Read-only — calls
/// <see cref="IOrderRepository.GetSummaryByIdAsync"/>, which projects to the DTO in EF.
/// The entity-returning <see cref="IOrderRepository.GetByIdAsync"/> stays for the saga
/// handlers (<c>PaymentCompletedHandler</c>, <c>PaymentFailedHandler</c>,
/// <c>ShipmentDispatchedHandler</c>) that mutate the aggregate. See
/// <c>docs/cqrs-data-access.md</c> for the read/write split rule.
/// </summary>
public record GetOrderByIdQuery(Guid OrderId);

public class GetOrderByIdHandler(IOrderRepository repository)
{
    public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
        => repository.GetSummaryByIdAsync(request.OrderId, cancellationToken);
}
