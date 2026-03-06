using System.Diagnostics.Metrics;
using NextAurora.Contracts.Events;
using PaymentService.Application.Commands;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Application.Handlers;

public class ProcessPaymentHandler(
    IPaymentRepository repository,
    IPaymentGateway gateway,
    IEventPublisher eventPublisher)
{
    private static readonly Counter<long> PaymentsProcessed =
        new Meter("NextAurora").CreateCounter<long>("payments.processed");

    public async Task<Guid> Handle(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

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
            }, cancellationToken);

            PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        else
        {
            payment.MarkAsFailed(result.ErrorMessage ?? "Unknown error");
            await repository.UpdateAsync(payment, cancellationToken);

            await eventPublisher.PublishAsync(new PaymentFailedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                BuyerId = request.BuyerId,
                Reason = result.ErrorMessage ?? "Unknown error",
                FailedAt = DateTime.UtcNow
            }, cancellationToken);

            PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        }

        return payment.Id;
    }
}
