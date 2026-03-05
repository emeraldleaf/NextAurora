using MediatR;

namespace ShippingService.Application.Commands;

public record CreateShipmentCommand(Guid OrderId) : IRequest<Guid>;
