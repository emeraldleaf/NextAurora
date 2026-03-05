namespace PaymentService.Domain.Entities;

public class Payment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentStatus Status { get; private set; }
    public string Provider { get; private set; } = "";
    public string? ExternalTransactionId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }

    private Payment() { }

    public static Payment Create(Guid orderId, decimal amount, string currency, string provider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (orderId == Guid.Empty)
            throw new ArgumentException("Order ID must not be empty.", nameof(orderId));

        return new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            Provider = provider,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkAsCompleted(string externalTransactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTransactionId);

        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException("Cannot complete a payment that is not pending.");

        Status = PaymentStatus.Completed;
        ExternalTransactionId = externalTransactionId;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException("Cannot fail a payment that is not pending.");

        Status = PaymentStatus.Failed;
        FailureReason = reason;
    }
}
