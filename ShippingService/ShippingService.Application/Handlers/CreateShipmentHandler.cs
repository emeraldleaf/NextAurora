using MediatR;
using NovaCraft.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Application.Handlers;

public class CreateShipmentHandler(
    IShipmentRepository repository,
    IEventPublisher eventPublisher) : IRequestHandler<CreateShipmentCommand, Guid>
{
    private static readonly string[] Carriers = ["FedEx", "UPS", "USPS", "DHL"];

    public async Task<Guid> Handle(CreateShipmentCommand request, CancellationToken cancellationToken)
    {
        var carrier = Carriers[Random.Shared.Next(Carriers.Length)];
        var shipment = Shipment.Create(request.OrderId, carrier);
        shipment.Dispatch();

        await repository.AddAsync(shipment, cancellationToken);

        await eventPublisher.PublishAsync(new ShipmentDispatchedEvent
        {
            ShipmentId = shipment.Id,
            OrderId = shipment.OrderId,
            Carrier = shipment.Carrier,
            TrackingNumber = shipment.TrackingNumber,
            DispatchedAt = shipment.DispatchedAt!.Value
        }, "shipping-events", cancellationToken);

        return shipment.Id;
    }
}
