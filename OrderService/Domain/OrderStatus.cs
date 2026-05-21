namespace OrderService.Domain;

/// <summary>
/// The order lifecycle states. Transitions are enforced by the methods on <see cref="Order"/> —
/// each transition checks the current status before changing it. The state diagram:
/// <code>
///         Placed ──MarkAsPaid──→ Paid ──MarkAsShipped──→ Shipped ──→ Delivered
///            │
///            ├──MarkAsPaymentFailed──→ PaymentFailed   (terminal)
///            │
///            └──Cancel──→ Cancelled  (allowed from any non-shipped state)
/// </code>
/// We persist this enum as a string in the database (see <c>OrderDbContext</c>) so re-ordering
/// or renaming entries doesn't silently corrupt rows — the column stores "Placed", not "0".
/// </summary>
public enum OrderStatus
{
    Placed,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
    PaymentFailed
}
