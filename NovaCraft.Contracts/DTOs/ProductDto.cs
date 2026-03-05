namespace NovaCraft.Contracts.DTOs;

public record ProductDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public string Category { get; init; } = "";
    public string SellerId { get; init; } = "";
    public int StockQuantity { get; init; }
    public bool IsAvailable { get; init; }
}
