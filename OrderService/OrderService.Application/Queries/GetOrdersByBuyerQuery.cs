using MediatR;
using NextAurora.Contracts.DTOs;

namespace OrderService.Application.Queries;

public record GetOrdersByBuyerQuery(Guid BuyerId) : IRequest<IReadOnlyList<OrderSummaryDto>>;
