using OrderService.Domain.Entities;

namespace OrderService.Tests.Unit.Builders;

public class OrderBuilder
{
    private Guid _buyerId = Guid.NewGuid();
    private string _currency = "USD";
    private List<OrderLine> _lines = [OrderLineBuilder.Default().Build()];

    public static OrderBuilder Default() => new();

    public OrderBuilder WithBuyerId(Guid id) { _buyerId = id; return this; }
    public OrderBuilder WithCurrency(string c) { _currency = c; return this; }
    public OrderBuilder WithLines(List<OrderLine> l) { _lines = l; return this; }

    public Order Build() => Order.Create(_buyerId, _currency, _lines);
}
