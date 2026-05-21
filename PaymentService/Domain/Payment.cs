namespace PaymentService.Domain;

/// <summary>
/// The Payment aggregate root: one Payment per Order (enforced by a unique index in
/// <c>PaymentDbContext</c>). State machine is <c>Pending → Completed</c> or <c>Pending → Failed</c>;
/// both are terminal. <see cref="ExternalTransactionId"/> records the provider's transaction ID
/// (e.g. the Stripe charge ID) so we can reconcile against their dashboard.
///
/// <para>
/// <b>Idempotency under saga retries:</b> the <c>OrderPlacedHandler</c> in this service first
/// checks if a Payment already exists for the order before creating one, and the status guards
/// in <see cref="MarkAsCompleted"/> / <see cref="MarkAsFailed"/> prevent double-processing if
/// the Service Bus redelivers a message. Combined with the <c>OrderId</c> unique index, we get
/// "exactly-once outcome" even with at-least-once delivery.
/// </para>
/// </summary>
public class Payment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentStatus Status { get; private set; }
    public string Provider { get; private set; } = "";

    // The provider's identifier for the actual money movement — e.g. Stripe's <c>ch_...</c>.
    // Stored so we can map back to their records during disputes or reconciliation.
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

    /// <summary>
    /// Called after the gateway confirms the charge. Status guard ensures we don't double-complete
    /// on a redelivered event.
    /// </summary>
    public void MarkAsCompleted(string externalTransactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalTransactionId);

        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException("Cannot complete a payment that is not pending.");

        Status = PaymentStatus.Completed;
        ExternalTransactionId = externalTransactionId;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Called when the gateway rejects the charge. <see cref="FailureReason"/> is stored for
    /// debugging and audit but is never returned in API responses verbatim — it can contain
    /// provider error codes that aren't safe to expose to end users.
    /// </summary>
    public void MarkAsFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException("Cannot fail a payment that is not pending.");

        Status = PaymentStatus.Failed;
        FailureReason = reason;
    }
}
