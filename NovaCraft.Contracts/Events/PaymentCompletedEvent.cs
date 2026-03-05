namespace NovaCraft.Contracts.Events;

public record PaymentCompletedEvent
{
    public Guid PaymentId { get; init; }
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
    public string Provider { get; init; } = "";
    public DateTime CompletedAt { get; init; }
}
