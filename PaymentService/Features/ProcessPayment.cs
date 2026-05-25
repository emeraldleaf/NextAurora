using System.Diagnostics.Metrics;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.Events;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Features;

/// <summary>
/// "Process payment" vertical slice: command + validator + handler co-located.
///
/// <para>Two ways into this handler:</para>
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
/// if two redeliveries race past the existence check at the same instant: the second insert
/// throws <see cref="DbUpdateException"/>, we catch it, re-fetch the winning Payment, and
/// return its ID. Net effect: at-least-once delivery + concurrent inserts still produce
/// exactly one Payment row per order.
/// </para>
/// </summary>
public record ProcessPaymentCommand(Guid OrderId, decimal Amount, string Currency, Guid BuyerId);

public class ProcessPaymentCommandValidator : AbstractValidator<ProcessPaymentCommand>
{
    public ProcessPaymentCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.BuyerId).NotEmpty();
    }
}

public class ProcessPaymentHandler(
    PaymentDbContext context,
    IPaymentGateway gateway,
    IEventPublisher eventPublisher)
{
    private static readonly Counter<long> PaymentsProcessed =
        new Meter("NextAurora").CreateCounter<long>("payments.processed");

    public async Task<Guid> HandleAsync(ProcessPaymentCommand request, CancellationToken cancellationToken)
    {
        // Idempotency check — see class summary.
        var existing = await context.Payments
            .FirstOrDefaultAsync(p => p.OrderId == request.OrderId, cancellationToken);
        if (existing is not null)
            return existing.Id;

        // Create the Payment in Pending state, persist it, THEN call the gateway. We persist
        // before charging so we have a record even if the gateway call hangs and the process
        // dies — the PaymentRecoveryJob sweeper picks up stuck Pendings and marks them Failed.
        var payment = Payment.Create(request.OrderId, request.BuyerId, request.Amount, request.Currency, "Stripe");
        await context.Payments.AddAsync(payment, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The pre-check above races with concurrent deliveries: two messages can both see
            // "no existing payment" and both try to insert. The unique index on OrderId catches
            // the loser. Detach our about-to-be-orphaned entity, re-fetch the winner, and
            // return its ID. Without this catch, the redelivery model leaks DbUpdateException
            // to Wolverine's retry loop on every concurrent insert.
            context.Entry(payment).State = EntityState.Detached;
            var racedExisting = await context.Payments
                .FirstOrDefaultAsync(p => p.OrderId == request.OrderId, cancellationToken);
            if (racedExisting is not null)
                return racedExisting.Id;
            throw;
        }

        var result = await gateway.ProcessPaymentAsync(request.Amount, request.Currency, cancellationToken);

        if (result.Success)
        {
            // Mutate via domain method (status guard validates we're still in Pending), then
            // persist. The domain entity owns the rule "only Pending can complete" — the
            // handler doesn't restate it.
            payment.MarkAsCompleted(result.TransactionId);
            await context.SaveChangesAsync(cancellationToken);

            await eventPublisher.PublishAsync(new PaymentCompletedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                BuyerId = request.BuyerId,
                Amount = payment.Amount,
                Provider = payment.Provider,
                CompletedAt = payment.CompletedAt!.Value
            }, cancellationToken);

            PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        else
        {
            payment.MarkAsFailed(result.ErrorMessage ?? "Unknown error");
            await context.SaveChangesAsync(cancellationToken);

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
