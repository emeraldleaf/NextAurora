using System.Diagnostics.Metrics;
using NextAurora.Contracts.Events;
using PaymentService.Application.Commands;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Application.Handlers;

/// <summary>
/// Handles the <see cref="ProcessPaymentCommand"/>. Two ways into this handler:
/// <list type="bullet">
///   <item>HTTP endpoint <c>POST /api/v1/payments/process</c> (admin/manual processing).</item>
///   <item>The saga: <c>OrderPlacedHandler</c> in this service receives <c>OrderPlacedEvent</c>
///         from Service Bus and invokes this command via Wolverine.</item>
/// </list>
///
/// <para>
/// <b>Idempotency:</b> the very first thing we do is look for an existing Payment for this
/// order ID. If one exists, we return its ID and stop — we don't double-charge. This handles
/// every redelivery scenario: Service Bus retries, DLQ replays, or an admin POSTing twice.
/// The unique index on <c>OrderId</c> in <c>PaymentDbContext</c> is the database-level backstop
/// if two redeliveries race past the existence check at the same instant.
/// </para>
/// <para>
/// <b>Anti-corruption layer:</b> <c>IPaymentGateway</c> is our internal abstraction — its
/// implementation (<c>StripePaymentGateway</c>) is what knows about the third-party API. The
/// handler depends on the abstraction, not Stripe directly. If we add a second provider or
/// swap Stripe for Adyen, the handler doesn't change.
/// </para>
/// <para>
/// <b>Outcome split:</b> success and failure both produce events
/// (<see cref="PaymentCompletedEvent"/> / <see cref="PaymentFailedEvent"/>) so downstream
/// services (OrderService, ShippingService, NotificationService) can react. The metric counter
/// is tagged with the <c>outcome</c> dimension for dashboard slicing.
/// </para>
/// </summary>
public class ProcessPaymentHandler(
    IPaymentRepository repository,
    IPaymentGateway gateway,
    IEventPublisher eventPublisher)
{
    private static readonly Counter<long> PaymentsProcessed =
        new Meter("NextAurora").CreateCounter<long>("payments.processed");

    public async Task<Guid> Handle(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        // Idempotency check — see class summary.
        var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

        // Create the Payment in Pending state, persist it, THEN call the gateway. We persist
        // before charging so we have a record even if the gateway call hangs and the process
        // dies — the next redelivery will see the Pending Payment and... actually, see the
        // existence check above, which means we'd no-op. That's a known gap: a Pending Payment
        // that's stuck (the process died mid-gateway-call) will never advance. Real-world fix
        // would be a sweeper job that picks up Pendings older than N minutes and either retries
        // or marks them Failed. Out of scope today.
        var payment = Payment.Create(request.OrderId, request.Amount, request.Currency, "Stripe");
        await repository.AddAsync(payment, cancellationToken);

        var result = await gateway.ProcessPaymentAsync(request.Amount, request.Currency, cancellationToken);

        if (result.Success)
        {
            // Mutate via domain method (status guard validates we're still in Pending), then
            // persist. The domain entity owns the rule "only Pending can complete" — the
            // handler doesn't restate it.
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

            // PaymentFailedEvent carries the reason verbatim — the buyer-facing notification
            // will use a generic message; this raw reason is for OrderService's audit trail and
            // is logged but never returned to clients.
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
