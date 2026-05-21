namespace PaymentService.Domain;

public interface IPaymentGateway
{
    Task<PaymentGatewayResult> ProcessPaymentAsync(decimal amount, string currency, CancellationToken ct = default);
}

public record PaymentGatewayResult(bool Success, string TransactionId, string? ErrorMessage = null);
