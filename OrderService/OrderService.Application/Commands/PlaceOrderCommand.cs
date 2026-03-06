namespace OrderService.Application.Commands;

public record PlaceOrderCommand(
    Guid BuyerId,
    string Currency,
    List<PlaceOrderLineItem> Lines);

public record PlaceOrderLineItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);
