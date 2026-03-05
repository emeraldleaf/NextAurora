using Microsoft.Extensions.Logging;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Infrastructure.Gateway;

public class StripePaymentGateway(ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    public async Task<PaymentGatewayResult> ProcessPaymentAsync(decimal amount, string currency, CancellationToken ct = default)
    {
        // Anti-Corruption Layer: isolates Stripe's API model from our domain
        // In production, this would call Stripe's API via their SDK
        logger.LogInformation("Processing payment of {Amount} {Currency} via Stripe", amount, currency);

        // Simulate payment processing
        await Task.Delay(100, ct);

        var transactionId = $"stripe_txn_{Guid.NewGuid():N}";
        return new PaymentGatewayResult(true, transactionId);
    }
}
