using Bogus;
using OrderService.Domain.Entities;

namespace OrderService.Tests.Unit.Builders;

public class OrderLineBuilder
{
    private static readonly Faker Faker = new();
    private Guid _productId = Guid.NewGuid();
    private string _productName = Faker.Commerce.ProductName();
    private int _quantity = 1;
    private decimal _unitPrice = 9.99m;

    public static OrderLineBuilder Default() => new();

    public OrderLineBuilder WithProductId(Guid id) { _productId = id; return this; }
    public OrderLineBuilder WithProductName(string name) { _productName = name; return this; }
    public OrderLineBuilder WithQuantity(int q) { _quantity = q; return this; }
    public OrderLineBuilder WithUnitPrice(decimal p) { _unitPrice = p; return this; }

    public OrderLine Build() => OrderLine.Create(_productId, _productName, _quantity, _unitPrice);
}
