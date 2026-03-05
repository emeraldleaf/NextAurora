namespace NextAurora.Contracts.Events;

public record OrderPlacedEvent
{
    public Guid OrderId { get; init; }
    public Guid BuyerId { get; init; }
    public DateTime PlacedAt { get; init; }
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public List<OrderLineContract> Lines { get; init; } = [];
}

public record OrderLineContract
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}
