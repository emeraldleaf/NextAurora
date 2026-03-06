namespace OrderService.Domain.Entities;

public enum OrderStatus
{
    Placed,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
    // Payment was attempted but the gateway rejected it (insufficient funds, expired card, etc.).
    // The order stays in this terminal state; the buyer must place a new order or retry payment.
    PaymentFailed
}
