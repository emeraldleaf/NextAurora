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

    public void MarkAsProcessed() => Status = RefundStatus.Processed;
    public void MarkAsFailed() => Status = RefundStatus.Failed;
}
