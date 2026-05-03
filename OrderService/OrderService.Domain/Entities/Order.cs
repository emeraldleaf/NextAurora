namespace OrderService.Domain.Entities;

/// <summary>
/// The Order aggregate root. In DDD terms, an "aggregate" is a cluster of objects treated as a
/// single unit for data changes and consistency: <see cref="Order"/> is the root, and
/// <see cref="OrderLine"/>s are children only reachable through it. Outside code never reaches
/// past the root to mutate a line directly — that's how we keep the order's invariants
/// (e.g. "TotalAmount equals the sum of its lines") true at all times.
///
/// <para>
/// <b>SOLID — Single Responsibility:</b> this class owns one thing — the rules that make a valid
/// Order in our business: a buyer must exist, currency must be set, at least one line, and state
/// transitions follow the saga (<c>Placed → Paid → Shipped → Delivered</c> with branches for
/// cancellation and payment failure). It does not know about the database, HTTP, or messaging.
/// Those concerns live in Application/Infrastructure layers — Domain has zero references outward.
/// </para>
/// <para>
/// <b>SOLID — Open/Closed:</b> behavior changes through new methods (e.g. adding a future
/// <c>MarkAsRefunded</c>) rather than by adding flags or branches inside existing ones.
/// </para>
/// <para>
/// <b>Encapsulation:</b> all setters are private. State only changes through the public methods
/// below, each of which validates the current status before transitioning. This is what makes
/// it impossible for an Order to silently end up in an illegal state — the compiler enforces it.
/// </para>
/// </summary>
public class Order
{
    // Properties have private setters: outside code can read state but can't assign to it.
    // The only paths to mutation are the factory and the named transition methods.
    public Guid Id { get; private set; }
    public Guid BuyerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTime PlacedAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public DateTime? ShippedAt { get; private set; }

    // Private backing field + read-only public projection.
    // Why not just `public List<OrderLine> Lines { get; }`? Because callers could then call
    // `order.Lines.Add(...)` and bypass our invariants. With `IReadOnlyList<T>`, mutation is
    // a compile error. New lines can only be added through methods we own (none today, by
    // design — orders are immutable once placed).
    private readonly List<OrderLine> _lines = [];
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    // Parameterless constructor exists for one reason only: EF Core needs to materialize
    // entities from the database, and it does that by calling the parameterless constructor
    // and then setting properties via reflection (with private setters). We make it private
    // so application code can't accidentally bypass `Create()` and produce an invalid Order.
    private Order() { }

    /// <summary>
    /// Factory method — the only way for application code to create a new Order. The reason
    /// this isn't a public constructor: a constructor's job is to build an object, but it can't
    /// fail descriptively. Throwing from a constructor is allowed but awkward to read; a named
    /// static method makes the validation visible and gives us a single chokepoint for invariants.
    /// </summary>
    public static Order Create(Guid buyerId, string currency, List<OrderLine> lines)
    {
        // Guard clauses: every input is checked before we do any work. If any of these throw,
        // no Order is ever created in an invalid state — there's nothing to clean up.
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

        // Server-side total: TotalAmount is *derived* from the lines, never trusted from the
        // client. If we accepted client-submitted totals, anyone could place an order for $0.01.
        // This is a security rule disguised as a domain rule — money is calculated here, period.
        order.TotalAmount = lines.Sum(l => l.Quantity * l.UnitPrice);
        return order;
    }

    /// <summary>
    /// Transition: <c>Placed → Paid</c>. Triggered by the saga when <c>PaymentCompletedEvent</c>
    /// is received. The status guard is the idempotency mechanism: if this event arrives twice
    /// (Service Bus is at-least-once, redelivery happens), the second call throws and the
    /// handler treats it as a no-op rather than corrupting state.
    /// </summary>
    public void MarkAsPaid()
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Cannot mark order as paid in the current status.");
        Status = OrderStatus.Paid;
        PaidAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Transition: <c>Paid → Shipped</c>. Same idempotency story — must be Paid first.
    /// </summary>
    public void MarkAsShipped()
    {
        if (Status != OrderStatus.Paid)
            throw new InvalidOperationException("Cannot mark order as shipped in the current status.");
        Status = OrderStatus.Shipped;
        ShippedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Transition: <c>Placed → PaymentFailed</c>. Terminal state for a failed payment.
    /// </summary>
    public void MarkAsPaymentFailed()
    {
        // Payment can only fail while the order is still awaiting payment.
        // If the order already moved to Paid (e.g. a duplicate PaymentFailedEvent from a DLQ
        // replay), we silently ignore — the handler checks status before calling this.
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Cannot mark payment as failed in the current status.");
        Status = OrderStatus.PaymentFailed;
    }

    /// <summary>
    /// Cancel: allowed from any state except after the order has shipped. Once it's in transit
    /// or delivered, cancellation has to go through a refund/return flow (not modeled yet).
    /// </summary>
    public void Cancel()
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
            throw new InvalidOperationException("Cannot cancel order in the current status.");
        Status = OrderStatus.Cancelled;
    }
}
