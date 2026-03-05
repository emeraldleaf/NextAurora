namespace OrderService.Domain.Entities;

public enum OrderStatus
{
    Placed,
    Paid,
    Shipped,
    Delivered,
    Cancelled
}
