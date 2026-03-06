namespace NextAurora.Contracts.Events;

public record PaymentFailedEvent
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    // BuyerId is included so downstream consumers (NotificationService) can look up the
    // buyer's contact details without needing to call back to OrderService.
    public Guid BuyerId { get; init; }
    public string Reason { get; init; } = "";
    public DateTime FailedAt { get; init; }
}
