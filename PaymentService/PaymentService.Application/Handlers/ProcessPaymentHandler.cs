using MediatR;
using NovaCraft.Contracts.Events;
using PaymentService.Application.Commands;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Application.Handlers;

public class ProcessPaymentHandler(
    IPaymentRepository repository,
    IPaymentGateway gateway,
    IEventPublisher eventPublisher) : IRequestHandler<ProcessPaymentCommand, Guid>
{
    public async Task<Guid> Handle(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        var payment = Payment.Create(request.OrderId, request.Amount, request.Currency, "Stripe");
        await repository.AddAsync(payment, cancellationToken);

        var result = await gateway.ProcessPaymentAsync(request.Amount, request.Currency, cancellationToken);

        if (result.Success)
        {
            payment.MarkAsCompleted(result.TransactionId);
            await repository.UpdateAsync(payment, cancellationToken);

            await eventPublisher.PublishAsync(new PaymentCompletedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                Amount = payment.Amount,
                Provider = payment.Provider,
                CompletedAt = payment.CompletedAt!.Value
            }, "payment-events", cancellationToken);
        }
        else
        {
            payment.MarkAsFailed(result.ErrorMessage ?? "Unknown error");
            await repository.UpdateAsync(payment, cancellationToken);

            await eventPublisher.PublishAsync(new PaymentFailedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                Reason = result.ErrorMessage ?? "Unknown error",
                FailedAt = DateTime.UtcNow
            }, "payment-events", cancellationToken);
        }

        return payment.Id;
    }
}
