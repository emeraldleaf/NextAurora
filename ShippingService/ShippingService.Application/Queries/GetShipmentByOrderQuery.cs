namespace ShippingService.Application.Queries;

public record GetShipmentByOrderQuery(Guid OrderId);

public record ShipmentDto(
    Guid Id,
    Guid OrderId,
    string Carrier,
    string TrackingNumber,
    string Status,
    DateTime CreatedAt,
    DateTime? DispatchedAt,
    List<TrackingEventDto> TrackingEvents);

public record TrackingEventDto(string Description, string Status, DateTime OccurredAt);
