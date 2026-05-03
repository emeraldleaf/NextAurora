namespace OrderService.Application.Queries;

public record GetOrdersByBuyerQuery(Guid BuyerId, int Page = 1, int PageSize = 50);
