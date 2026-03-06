namespace PaymentService.Domain.Entities;

public class Refund
{
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public decimal Amount { get; private set; }
    public string Reason { get; private set; } = "";
    public RefundStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Refund() { }

    public static Refund Create(Guid paymentId, decimal amount, string reason)
    {
        if (paymentId == Guid.Empty)
            throw new ArgumentException("Payment ID must not be empty.", nameof(paymentId));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new Refund
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            Amount = amount,
            Reason = reason,
            Status = RefundStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkAsProcessed()
    {
        if (Status != RefundStatus.Pending)
            throw new InvalidOperationException("Cannot mark refund as processed in the current status.");
        Status = RefundStatus.Processed;
    }

    public void MarkAsFailed()
    {
        if (Status != RefundStatus.Pending)
            throw new InvalidOperationException("Cannot mark refund as failed in the current status.");
        Status = RefundStatus.Failed;
    }
}
