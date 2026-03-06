using NextAurora.Contracts.DTOs;

namespace OrderService.Application.Queries;

public record GetOrderByIdQuery(Guid OrderId);
