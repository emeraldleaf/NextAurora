using Bogus;
using CatalogService.Domain.Entities;

namespace CatalogService.Tests.Unit.Builders;

public class ProductBuilder
{
    private static readonly Faker Faker = new();
    private string _name = Faker.Commerce.ProductName();
    private readonly string _description = Faker.Commerce.ProductDescription();
    private decimal _price = 29.99m;
    private readonly string _currency = "USD";
    private Guid _categoryId = Guid.NewGuid();
    private readonly string _sellerId = Guid.NewGuid().ToString();
    private int _stockQuantity = 10;

    public static ProductBuilder Default() => new();

    public ProductBuilder WithName(string n) { _name = n; return this; }
    public ProductBuilder WithPrice(decimal p) { _price = p; return this; }
    public ProductBuilder WithStockQuantity(int q) { _stockQuantity = q; return this; }
    public ProductBuilder WithCategoryId(Guid id) { _categoryId = id; return this; }

    public Product Build() => Product.Create(_name, _description, _price, _currency, _categoryId, _sellerId, _stockQuantity);
}
