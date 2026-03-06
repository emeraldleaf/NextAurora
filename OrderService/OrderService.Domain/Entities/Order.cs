namespace OrderService.Domain.Entities;

public class Order
{
    public Guid Id { get; private set; }
    public Guid BuyerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTime PlacedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? ShippedAt { get; private set; }

    private readonly List<OrderLine> _lines = [];
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    private Order() { }

    public static Order Create(Guid buyerId, string currency, List<OrderLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(lines);

        if (buyerId == Guid.Empty)
            throw new ArgumentException("Buyer ID must not be empty.", nameof(buyerId));

        if (lines.Count == 0)
            throw new ArgumentException("Order must contain at least one line.", nameof(lines));

        var order = new Order
        {
            Id = Guid.NewGuid(),
            BuyerId = buyerId,
            Status = OrderStatus.Placed,
            Currency = currency,
            PlacedAt = DateTime.UtcNow
        };
        order._lines.AddRange(lines);
        order.TotalAmount = lines.Sum(l => l.Quantity * l.UnitPrice);
        return order;
    }

    public void MarkAsPaid()
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Cannot mark order as paid in the current status.");
        Status = OrderStatus.Paid;
        PaidAt = DateTime.UtcNow;
    }

    public void MarkAsShipped()
    {
        if (Status != OrderStatus.Paid)
            throw new InvalidOperationException("Cannot mark order as shipped in the current status.");
        Status = OrderStatus.Shipped;
        ShippedAt = DateTime.UtcNow;
    }

    public void MarkAsPaymentFailed()
    {
        // Payment can only fail while the order is still awaiting payment.
        // If the order already moved to Paid (e.g. a duplicate PaymentFailedEvent from a DLQ
        // replay), we silently ignore — the handler checks status before calling this.
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Cannot mark payment as failed in the current status.");
        Status = OrderStatus.PaymentFailed;
    }

    public void Cancel()
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
            throw new InvalidOperationException("Cannot cancel order in the current status.");
        Status = OrderStatus.Cancelled;
    }
}
