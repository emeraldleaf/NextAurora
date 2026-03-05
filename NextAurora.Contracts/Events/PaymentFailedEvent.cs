namespace NextAurora.Contracts.Events;

public record PaymentFailedEvent
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public string Reason { get; init; } = "";
    public DateTime FailedAt { get; init; }
}
