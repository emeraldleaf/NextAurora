namespace PaymentService.Domain;

/// <summary>
/// Domain-shaped port for the payment gateway. Hides the provider SDK
/// (Stripe / Adyen / Braintree) behind a tiny interface; callers pass amounts and an
/// idempotency key, get back a result.
///
/// <para>
/// <b>Why <paramref name="idempotencyKey"/> is required, not optional.</b> The Gateway
/// handler (<c>PaymentProcessingRequestedHandler</c>) runs on Wolverine's at-least-once
/// delivery semantics — the broker or the local in-memory queue can hand the same
/// <c>PaymentProcessingRequested</c> message to the handler more than once (process death
/// after a successful Stripe call but before <c>MarkAsCompleted</c> is the canonical
/// race). Without an idempotency key, redelivery makes the second Stripe call too — the
/// buyer gets charged twice. With a key derived from the stable <c>Payment.Id</c>, Stripe
/// recognizes the duplicate request, skips the charge, and returns the original response.
/// </para>
/// <para>
/// <b>Provider semantics</b> (Stripe — but the contract is similar for Adyen / Braintree):
/// every call must carry an <c>Idempotency-Key</c> header. Stripe stores the key + response
/// for 24 hours; identical keys within that window return the cached response without
/// re-executing the call. After 24h the key can be reused for a new request, but our
/// Wolverine retry budget closes well before that. Stripe accepts any UTF-8 string ≤ 255
/// chars; we pass <c>Payment.Id.ToString()</c> (a Guid, 36 chars) — unique per payment,
/// stable across redeliveries.
/// </para>
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentGatewayResult> ProcessPaymentAsync(decimal amount, string currency, string idempotencyKey, CancellationToken ct = default);
}

public record PaymentGatewayResult(bool Success, string TransactionId, string? ErrorMessage = null);
