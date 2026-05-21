namespace OrderService.Domain;

/// <summary>
/// A single line on an order: one product, a quantity, and the unit price *at the time the order
/// was placed*. OrderLine is part of the <see cref="Order"/> aggregate — it has no independent
/// identity in the domain. Outside code never loads an OrderLine without its Order; we never
/// mutate one in isolation.
///
/// <para>
/// <b>Why we copy <c>ProductName</c> and <c>UnitPrice</c> here</b> instead of holding only
/// <c>ProductId</c> and joining to the Catalog later: orders are historical records. If a seller
/// changes a product's name or price after an order is placed, that order's line items must keep
/// showing what the buyer actually agreed to. This is intentional denormalization for correctness,
/// not a perf trick — though it also incidentally avoids a cross-service join at read time.
/// </para>
/// </summary>
public class OrderLine
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = "";
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    private OrderLine() { }

    /// <summary>
    /// Factory — the only way to create a line. Quantity must be positive; price must be
    /// non-negative (zero is allowed for promotional/free items, negative is never valid).
    /// </summary>
    public static OrderLine Create(Guid productId, string productName, int quantity, decimal unitPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);

        if (productId == Guid.Empty)
            throw new ArgumentException("Product ID must not be empty.", nameof(productId));

        return new OrderLine
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            UnitPrice = unitPrice
        };
    }
}
