using System.Diagnostics.Metrics;
using MediatR;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Domain.Entities;
using ShippingService.Domain.Interfaces;

namespace ShippingService.Application.Handlers;

public class CreateShipmentHandler(
    IShipmentRepository repository,
    IEventPublisher eventPublisher) : IRequestHandler<CreateShipmentCommand, Guid>
{
    private static readonly string[] Carriers = ["FedEx", "UPS", "USPS", "DHL"];

    private static readonly Counter<long> ShipmentsDispatched =
        new Meter("NextAurora").CreateCounter<long>("shipments.dispatched");

    public async Task<Guid> Handle(CreateShipmentCommand request, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

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

        ShipmentsDispatched.Add(1);
        return shipment.Id;
    }
}
