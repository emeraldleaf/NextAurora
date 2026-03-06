using MediatR;

namespace PaymentService.Application.Commands;

public record ProcessPaymentCommand(Guid OrderId, decimal Amount, string Currency, Guid BuyerId) : IRequest<Guid>;
