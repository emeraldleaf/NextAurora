namespace PaymentService.Domain.Entities;

/// <summary>
/// A refund issued against a completed <see cref="Payment"/>. Its own aggregate root rather than
/// a child of Payment because refunds have an independent lifecycle (pending → processed/failed)
/// and a payment may have multiple partial refunds over time.
///
/// <para>
/// Currently there's no command that creates a Refund — this entity exists to support a future
/// "Refund Processing" requirement (see BRD). Once a `RequestRefundCommand` lands, the handler
/// will load the parent Payment, validate it's <c>Completed</c>, then call <see cref="Create"/>.
/// </para>
/// </summary>
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
