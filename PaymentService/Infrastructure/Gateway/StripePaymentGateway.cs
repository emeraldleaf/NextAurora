using Microsoft.Extensions.Logging;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Gateway;

/// <summary>
/// Anti-corruption layer between our domain and the Stripe payment provider. The handler
/// depends on <see cref="IPaymentGateway"/> — a small, domain-shaped interface — and never
/// sees a <c>Stripe.Charge</c>, <c>Stripe.PaymentIntent</c>, or any other Stripe-specific
/// type. Tests substitute a fake <see cref="IPaymentGateway"/> without touching any HTTP or
/// SDK setup.
///
/// <para>
/// <b>Current state:</b> stub. The implementation just sleeps for 100ms and returns success
/// with a fake transaction ID. Replacing this with a real Stripe SDK call is a one-file change.
/// </para>
/// <para>
/// <b>What a real Stripe.net call would look like</b> (sketch — not active code):
/// <code>
/// var service = new PaymentIntentService();
/// var options = new RequestOptions { IdempotencyKey = idempotencyKey };
/// var intent = await service.CreateAsync(new PaymentIntentCreateOptions
/// {
///     Amount = (long)(amount * 100),    // Stripe takes integer minor units
///     Currency = currency.ToLowerInvariant(),
///     ConfirmationMethod = "automatic",
///     Confirm = true,
/// }, options, ct);
/// return new PaymentGatewayResult(intent.Status == "succeeded", intent.Id);
/// </code>
/// The <see cref="Stripe.RequestOptions.IdempotencyKey"/> property is the mechanism — it
/// turns into an <c>Idempotency-Key</c> HTTP header, Stripe deduplicates server-side.
/// </para>
/// </summary>
public partial class StripePaymentGateway(ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    public async Task<PaymentGatewayResult> ProcessPaymentAsync(decimal amount, string currency, string idempotencyKey, CancellationToken ct = default)
    {
        // Log the idempotency key so the stub's behavior is observable in dev/CI — when the
        // real Stripe call lands here, the same log line will help correlate retries with
        // Stripe's deduplication response in their dashboard.
        LogProcessingPayment(logger, amount, currency, idempotencyKey);

        // Stub: pretend we called Stripe's API. Real implementation would create a
        // PaymentIntent with the amount, currency, and idempotency key — see class XML doc.
        await Task.Delay(100, ct);

        var transactionId = $"stripe_txn_{Guid.NewGuid():N}";
        return new PaymentGatewayResult(true, transactionId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing payment of {Amount} {Currency} via Stripe (idempotency-key: {IdempotencyKey})")]
    private static partial void LogProcessingPayment(ILogger logger, decimal amount, string currency, string idempotencyKey);
}
