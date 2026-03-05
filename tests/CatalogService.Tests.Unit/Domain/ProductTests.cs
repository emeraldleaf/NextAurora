using CatalogService.Tests.Unit.Builders;
using FluentAssertions;

namespace CatalogService.Tests.Unit.Domain;

public class ProductTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsProduct()
    {
        var product = ProductBuilder.Default().Build();

        product.Id.Should().NotBeEmpty();
        product.Name.Should().NotBeNullOrWhiteSpace();
        product.Price.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Create_WithZeroPrice_ThrowsArgumentOutOfRangeException()
    {
        var act = () => ProductBuilder.Default().WithPrice(0m).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("price");
    }

    [Fact]
    public void Create_WithEmptyName_ThrowsArgumentException()
    {
        var act = () => ProductBuilder.Default().WithName("").Build();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithZeroStock_SetsIsAvailableFalse()
    {
        var product = ProductBuilder.Default().WithStockQuantity(0).Build();

        product.IsAvailable.Should().BeFalse();
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void Create_WithPositiveStock_SetsIsAvailableTrue()
    {
        var product = ProductBuilder.Default().WithStockQuantity(5).Build();

        product.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void AdjustStock_ToZero_SetsIsAvailableFalse()
    {
        var product = ProductBuilder.Default().WithStockQuantity(10).Build();

        product.AdjustStock(0);

        product.IsAvailable.Should().BeFalse();
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void AdjustStock_ToPositive_SetsIsAvailableTrue()
    {
        var product = ProductBuilder.Default().WithStockQuantity(0).Build();

        product.AdjustStock(5);

        product.IsAvailable.Should().BeTrue();
        product.StockQuantity.Should().Be(5);
    }

    [Fact]
    public void UpdateDetails_WithValidInputs_UpdatesFields()
    {
        var product = ProductBuilder.Default().Build();

        product.UpdateDetails("New Name", "New Desc", 49.99m);

        product.Name.Should().Be("New Name");
        product.Description.Should().Be("New Desc");
        product.Price.Should().Be(49.99m);
    }

    [Fact]
    public void UpdateDetails_SetsUpdatedAt()
    {
        var product = ProductBuilder.Default().Build();
        var before = DateTime.UtcNow;

        product.UpdateDetails("Updated", "Desc", 10m);

        product.UpdatedAt.Should().NotBeNull();
        product.UpdatedAt!.Value.Should().BeOnOrAfter(before);
    }
}
