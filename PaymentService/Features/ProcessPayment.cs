using System.Diagnostics.Metrics;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NextAurora.Contracts.Events;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;
using Wolverine;

namespace PaymentService.Features;

/// <summary>
/// "Process payment" vertical slice: split into an <b>Acceptor</b> + a <b>Gateway</b>
/// handler so the slow part (Stripe call) runs on the message bus instead of inside the
/// HTTP request or the OrderPlaced consumer.
///
/// <para><b>Why split.</b> The Stripe call is sub-second in the happy path but seconds-to-30s
/// on degraded gateway states. Doing it inline in <c>ProcessPaymentHandler</c> held the HTTP
/// request open (and a Wolverine handler slot + DbContext + broker message lease on the
/// saga path) for the entire duration. The 202 Accepted rule says: validate + persist
/// intent + publish a Wolverine message + return; let a follow-up handler do the slow work.
/// See CLAUDE.md "Performance Rules → Long-running work belongs on the message bus".</para>
///
/// <para><b>Two entry points still converge on <see cref="ProcessPaymentCommand"/>:</b></para>
/// <list type="bullet">
///   <item>HTTP endpoint <c>POST /api/v1/payments/process</c> (admin/manual processing).</item>
///   <item>The saga: <see cref="OrderPlacedHandler"/> returns a <see cref="ProcessPaymentCommand"/>
///         as a Wolverine cascading message.</item>
/// </list>
/// Both hit <see cref="ProcessPaymentHandler"/> (the Acceptor) which now does only the fast,
/// idempotent steps and publishes <see cref="PaymentProcessingRequested"/> for the gateway
/// handler to consume.
///
/// <para><b>Idempotency:</b> the Acceptor's first action is the same OrderId existence check
/// as before — if a Payment row already exists for this order, return its ID. On terminal
/// states (Completed/Failed) we still defensively re-publish the terminal event for saga
/// progress. On Pending we no-op the existence check (the in-flight gateway handler will
/// emit the terminal event when it finishes). The unique index on <c>OrderId</c> is the
/// database-level backstop for concurrent inserts; <see cref="DbUpdateException"/> recovery
/// detaches the loser entity and returns the winner's ID.</para>
///
/// <para><b>Outbox atomicity</b> on the Acceptor's path: <c>AddAsync</c> + <c>PublishAsync</c>
/// + <c>SaveChangesAsync</c> commit together — Payment(Pending) and the staged
/// <see cref="PaymentProcessingRequested"/> envelope land in one DB transaction. If the
/// process dies before the gateway handler runs, the existing <c>PaymentRecoveryJob</c>
/// sweeper still picks up stuck Pendings and marks them Failed (no behavior regression).</para>
///
/// <para><b>Idempotency on the Gateway handler</b> (re-deliveries of
/// <see cref="PaymentProcessingRequested"/>): two layers.
/// <list type="number">
///   <item><b>Local status guard:</b> pre-check <c>Payment.Status</c> — if not Pending,
///         return. Handles the common redelivery case (prior delivery already drove this
///         Payment through to Completed/Failed and saved).</item>
///   <item><b>Gateway-side idempotency key:</b> the call passes <c>Payment.Id.ToString()</c>
///         as Stripe's <c>Idempotency-Key</c>. Closes the "Stripe charged but process
///         crashed before MarkAsCompleted" race — on redelivery the status guard still
///         passes (Payment is still Pending), but Stripe recognizes the duplicate key
///         and returns the original response instead of re-charging. See
///         <see cref="IPaymentGateway"/> XML doc for provider semantics.</item>
/// </list></para>
/// </summary>
/// <summary>
/// HTTP request body for <c>POST /api/v1/payments/process</c>. <b>Does not include BuyerId</b> —
/// the endpoint reads the authenticated buyer from the JWT <c>NameIdentifier</c> claim and
/// constructs the internal <see cref="ProcessPaymentCommand"/>. Trusting <c>BuyerId</c> from the
/// request body would let any authenticated buyer mint payments attributed to other buyers
/// (CWE-639 / mass-assignment). See CLAUDE.md "Security Requirements → Server-controlled fields
/// are computed server-side."
/// </summary>
public record ProcessPaymentRequest(Guid OrderId, decimal Amount, string Currency);

/// <summary>
/// Internal command. <c>BuyerId</c> is set by the HTTP endpoint from the JWT claim, or by the
/// saga (<see cref="OrderPlacedHandler"/>) from the trusted <c>OrderPlacedEvent</c> — never
/// from a client-controlled HTTP body.
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

/// <summary>
/// Internal continuation message. Published by <see cref="ProcessPaymentHandler"/> and
/// consumed by <see cref="PaymentProcessingRequestedHandler"/> to actually invoke the
/// gateway. Lives in PaymentService's assembly only — not exported to
/// <c>NextAurora.Contracts</c> because no other service is supposed to subscribe to it.
/// </summary>
public record PaymentProcessingRequested(Guid PaymentId);

/// <summary>
/// Acceptor — fast-path handler for <see cref="ProcessPaymentCommand"/>. Idempotency check,
/// persist Pending, publish the continuation, return PaymentId. No gateway call. Sub-second.
/// </summary>
public class ProcessPaymentHandler(
    PaymentDbContext context,
    IEventPublisher eventPublisher)
{
    // IMessageContext is injected as a METHOD parameter (not via the constructor IEventPublisher):
    // Wolverine only enlists the message context it injects into the handler signature in the
    // handler's transaction. A constructor-injected IMessageBus/IMessageContext (what IEventPublisher
    // wraps) is NOT enlisted, so under Wolverine 6 a publish through it fires inline — the local
    // PaymentProcessingRequested continuation would reach the Gateway handler BEFORE Payment(Pending)
    // commits, and the Gateway would find no row (payment stuck Pending). Publishing the continuation
    // through the enlisted messageContext stages it in the same transaction and dispatches it only
    // after commit. See the Wolverine 5→6 upgrade notes (docs/project-decisions.md). See CLAUDE.md.
    public async Task<Guid> HandleAsync(ProcessPaymentCommand request, IMessageContext messageContext, CancellationToken cancellationToken)
    {
        // Idempotency check — see class summary.
        var existing = await context.Payments
            .FirstOrDefaultAsync(p => p.OrderId == request.OrderId, cancellationToken);
        if (existing is not null)
        {
            await RepublishTerminalEventAsync(existing, cancellationToken);
            return existing.Id;
        }

        var payment = Payment.Create(request.OrderId, request.BuyerId, request.Amount, request.Currency, "Stripe");
        await context.Payments.AddAsync(payment, cancellationToken);

        // Stage the continuation envelope BEFORE SaveChanges so Wolverine's outbox bridge commits
        // Payment(Pending) + PaymentProcessingRequested in one DB transaction. Uses the enlisted
        // messageContext (see method-parameter note above), NOT eventPublisher. See CLAUDE.md "Outbox atomicity".
        cancellationToken.ThrowIfCancellationRequested();
        await messageContext.PublishAsync(new PaymentProcessingRequested(payment.Id));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent acceptors raced on the unique-OrderId index. The loser's transaction
            // (including the staged envelope) rolls back. Detach the about-to-be-orphaned entity,
            // re-fetch the winning Payment, and return its ID. The winner already published its
            // own PaymentProcessingRequested — no further action needed here.
            context.Entry(payment).State = EntityState.Detached;
            var racedExisting = await context.Payments
                .FirstOrDefaultAsync(p => p.OrderId == request.OrderId, cancellationToken);
            if (racedExisting is not null)
                return racedExisting.Id;
            throw;
        }

        return payment.Id;
    }

    private async Task RepublishTerminalEventAsync(Payment payment, CancellationToken ct)
    {
        // Pending: no-op. Either the gateway handler is still in-flight (this duplicate
        // redelivery raced it), or the recovery sweeper will eventually mark the row Failed
        // and publish PaymentFailedEvent. Re-publishing on Pending would be premature.
        switch (payment.Status)
        {
            case PaymentStatus.Completed:
                await eventPublisher.PublishAsync(new PaymentCompletedEvent
                {
                    PaymentId = payment.Id,
                    OrderId = payment.OrderId,
                    BuyerId = payment.BuyerId,
                    Amount = payment.Amount,
                    Provider = payment.Provider,
                    CompletedAt = payment.CompletedAt!.Value
                }, ct);
                break;
            case PaymentStatus.Failed:
                // FailedAt isn't persisted on the aggregate; UtcNow for the re-publish stamp.
                // Downstream consumers' idempotency guards key on PaymentId + OrderId, not on
                // FailedAt — the timestamp difference doesn't affect saga correctness.
                await eventPublisher.PublishAsync(new PaymentFailedEvent
                {
                    PaymentId = payment.Id,
                    OrderId = payment.OrderId,
                    BuyerId = payment.BuyerId,
                    Reason = payment.FailureReason ?? "Unknown error",
                    FailedAt = DateTime.UtcNow
                }, ct);
                break;
            case PaymentStatus.Pending:
            default:
                break;
        }
    }
}

/// <summary>
/// Gateway handler — consumes <see cref="PaymentProcessingRequested"/> and does the actual
/// Stripe call + result publish. This is where the slow work lives. Runs on a Wolverine
/// worker, not on an HTTP thread.
///
/// <para>Wolverine's retry + throttle policies apply here. A transient Stripe failure can be
/// retried per the global policies (<c>AddConcurrencyRetry</c> + Wolverine's default
/// retry handling) without affecting the HTTP path that already returned 202.</para>
/// </summary>
public class PaymentProcessingRequestedHandler(
    PaymentDbContext context,
    IPaymentGateway gateway)
{
    private static readonly Counter<long> PaymentsProcessed =
        new Meter("NextAurora").CreateCounter<long>("payments.processed");

    // IMessageContext is a METHOD parameter so Wolverine enlists it in the handler's outbox
    // transaction; a constructor IMessageBus publishes inline under Wolverine 6 and would dispatch
    // PaymentCompletedEvent/PaymentFailedEvent before the MarkAsCompleted/MarkAsFailed mutation
    // commits — a money event leaked ahead of (or despite a rolled-back) state change. See the
    // Wolverine 5→6 upgrade notes (docs/project-decisions.md). See CLAUDE.md.
    public async Task HandleAsync(PaymentProcessingRequested message, IMessageContext messageContext, CancellationToken cancellationToken)
    {
        var payment = await context.Payments
            .FirstOrDefaultAsync(p => p.Id == message.PaymentId, cancellationToken);
        if (payment is null)
        {
            // Payment row vanished between Acceptor commit and Gateway handler — shouldn't
            // happen, but no-op rather than throw (would cause endless Wolverine retries).
            return;
        }

        // Idempotency under at-least-once delivery of PaymentProcessingRequested. If a prior
        // delivery already drove this Payment through the gateway, we'd be Completed or
        // Failed — the Acceptor's RepublishTerminalEventAsync is the recovery path for the
        // saga; here we just no-op so we don't double-charge.
        if (payment.Status != PaymentStatus.Pending)
            return;

        // Gateway-side idempotency: pass payment.Id as the Stripe Idempotency-Key. This
        // closes the "Stripe charged but process crashed before MarkAsCompleted" race — on
        // redelivery the status guard above passes (still Pending), but Stripe recognizes
        // the duplicate key and returns the original response without re-charging. See
        // IPaymentGateway XML doc for the provider semantics.
        var result = await gateway.ProcessPaymentAsync(payment.Amount, payment.Currency, payment.Id.ToString(), cancellationToken);

        if (result.Success)
        {
            payment.MarkAsCompleted(result.TransactionId);

            // Publish-before-save through the enlisted messageContext: the envelope is staged and
            // flushed to wolverine.outgoing_envelopes on the SaveChanges below — atomically with the
            // MarkAsCompleted mutation. See CLAUDE.md "Outbox atomicity".
            cancellationToken.ThrowIfCancellationRequested();
            await messageContext.PublishAsync(new PaymentCompletedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                BuyerId = payment.BuyerId,
                Amount = payment.Amount,
                Provider = payment.Provider,
                CompletedAt = payment.CompletedAt!.Value
            });

            await context.SaveChangesAsync(cancellationToken);

            PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        else
        {
            payment.MarkAsFailed(result.ErrorMessage ?? "Unknown error");

            cancellationToken.ThrowIfCancellationRequested();
            await messageContext.PublishAsync(new PaymentFailedEvent
            {
                PaymentId = payment.Id,
                OrderId = payment.OrderId,
                BuyerId = payment.BuyerId,
                Reason = result.ErrorMessage ?? "Unknown error",
                FailedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync(cancellationToken);

            PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        }
    }
}
