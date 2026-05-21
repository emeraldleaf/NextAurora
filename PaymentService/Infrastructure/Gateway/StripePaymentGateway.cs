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
/// </summary>
public partial class StripePaymentGateway(ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    public async Task<PaymentGatewayResult> ProcessPaymentAsync(decimal amount, string currency, CancellationToken ct = default)
    {
        LogProcessingPayment(logger, amount, currency);

        // Stub: pretend we called Stripe's API. Real implementation would create a
        // PaymentIntent with the amount and currency, confirm it, and translate the result.
        await Task.Delay(100, ct);

        var transactionId = $"stripe_txn_{Guid.NewGuid():N}";
        return new PaymentGatewayResult(true, transactionId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing payment of {Amount} {Currency} via Stripe")]
    private static partial void LogProcessingPayment(ILogger logger, decimal amount, string currency);
}
