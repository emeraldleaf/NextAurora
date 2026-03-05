namespace CatalogService.Domain.Entities;

public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public Guid CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public string SellerId { get; private set; } = "";
    public int StockQuantity { get; private set; }
    public bool IsAvailable { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private Product() { }

    public static Product Create(string name, string description, decimal price, string currency, Guid categoryId, string sellerId, int stockQuantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        ArgumentOutOfRangeException.ThrowIfNegative(stockQuantity);

        if (categoryId == Guid.Empty)
            throw new ArgumentException("Category ID must not be empty.", nameof(categoryId));

        return new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Price = price,
            Currency = currency,
            CategoryId = categoryId,
            SellerId = sellerId,
            StockQuantity = stockQuantity,
            IsAvailable = stockQuantity > 0,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void UpdateDetails(string name, string description, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        Name = name;
        Description = description;
        Price = price;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AdjustStock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);

        StockQuantity = quantity;
        IsAvailable = quantity > 0;
        UpdatedAt = DateTime.UtcNow;
    }
}
