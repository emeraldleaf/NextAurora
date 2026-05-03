using Microsoft.Extensions.Logging;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Infrastructure.Gateway;

/// <summary>
/// Anti-corruption layer between our domain and the Stripe payment provider. The Application
/// layer depends on <see cref="IPaymentGateway"/> — a small, domain-shaped interface — and
/// <i>never</i> sees a <c>Stripe.Charge</c>, <c>Stripe.PaymentIntent</c>, or any other
/// Stripe-specific type. That's the entire point of the ACL: Stripe's model and our domain's
/// model are kept separate so that:
///
/// <list type="bullet">
///   <item>If Stripe changes its API, only this file needs to change. Handlers stay untouched.</item>
///   <item>If we add a second gateway (Adyen, PayPal), each gets its own implementation of
///         <see cref="IPaymentGateway"/> and the dispatch logic picks one — handlers still
///         don't change. (Open/Closed.)</item>
///   <item>Tests substitute a fake <see cref="IPaymentGateway"/> without touching any HTTP or
///         SDK setup.</item>
/// </list>
///
/// <para>
/// <b>Current state:</b> stub. The implementation just sleeps for 100ms and returns success
/// with a fake transaction ID. Replacing this with a real Stripe SDK call is a one-file change.
/// </para>
/// <para>
/// <b>Source-generated logging:</b> the <c>[LoggerMessage]</c> attribute below produces a
/// compiled, allocation-free logging method at build time. For production hot paths (which a
/// payment gateway call would be), this is meaningfully cheaper than <c>logger.LogInformation</c>
/// with interpolated strings. Even though we only log once per call here, using the generated
/// pattern signals "this is the right way" to anyone reading.
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

    // Source-generated, zero-allocation logging. See class summary.
    [LoggerMessage(Level = LogLevel.Information, Message = "Processing payment of {Amount} {Currency} via Stripe")]
    private static partial void LogProcessingPayment(ILogger logger, decimal amount, string currency);
}
