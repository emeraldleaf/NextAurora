namespace CatalogService.Domain.Entities;

/// <summary>
/// The Product aggregate root in CatalogService. Owned by a single seller, listed in a category,
/// with a price and stock count. Like all our aggregates, state is private and changes only
/// through named methods — the business rule "<c>IsAvailable</c> tracks <c>StockQuantity &gt; 0</c>"
/// stays true because both fields are only written together inside this class.
///
/// <para>
/// <b>Cross-service relationship:</b> when an order is placed, OrderService calls CatalogService
/// over gRPC to validate the product (exists, available, has enough stock) and reserves stock.
/// Catalog is the source of truth for "is this real and orderable"; OrderService never assumes.
/// </para>
/// </summary>
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

    /// <summary>
    /// Factory: validates everything up-front. Price must be strictly positive (a $0 product
    /// usually means a configuration mistake; if a free promo is needed in the future, that's
    /// a separate concept like a coupon, not a $0 list price). Stock can be zero — that's a
    /// real catalog state ("listed but out of stock").
    /// </summary>
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
            // Derived field: kept in sync with stock so consumers (gRPC clients, search) don't
            // each have to recompute it. The invariant is enforced by being only set here and
            // in AdjustStock — never via a public setter.
            IsAvailable = stockQuantity > 0,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Edit details a seller can change post-creation. Note we don't allow changing seller,
    /// category, or currency here — those are listing-level decisions; changing them would
    /// effectively be "unlist this product and create a new one". Keep simple operations
    /// simple; complex operations explicit.
    /// </summary>
    public void UpdateDetails(string name, string description, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        Name = name;
        Description = description;
        Price = price;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Replaces stock quantity outright (not delta-based). Used by OrderService over gRPC after
    /// a successful reservation. Both stock and the derived <c>IsAvailable</c> are updated
    /// together so the invariant holds.
    /// </summary>
    public void AdjustStock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);

        StockQuantity = quantity;
        IsAvailable = quantity > 0;
        UpdatedAt = DateTime.UtcNow;
    }
}
