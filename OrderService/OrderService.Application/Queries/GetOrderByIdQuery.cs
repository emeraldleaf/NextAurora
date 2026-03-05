using MediatR;
using NovaCraft.Contracts.DTOs;

namespace OrderService.Application.Queries;

public record GetOrderByIdQuery(Guid OrderId) : IRequest<OrderSummaryDto?>;
