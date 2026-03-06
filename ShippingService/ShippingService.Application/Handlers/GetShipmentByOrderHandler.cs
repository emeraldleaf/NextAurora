using ShippingService.Application.Queries;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Application.Handlers;

public class GetShipmentByOrderHandler(IShipmentRepository repository)
{
    public async Task<ShipmentDto?> Handle(GetShipmentByOrderQuery request, CancellationToken cancellationToken)
    {
        var shipment = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (shipment is null) return null;

        return new ShipmentDto(
            shipment.Id,
            shipment.OrderId,
            shipment.Carrier,
            shipment.TrackingNumber,
            shipment.Status.ToString(),
            shipment.CreatedAt,
            shipment.DispatchedAt,
            shipment.TrackingEvents.Select(e =>
                new TrackingEventDto(e.Description, e.Status, e.OccurredAt)).ToList());
    }
}
