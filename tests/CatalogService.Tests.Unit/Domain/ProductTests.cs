using CatalogService.Tests.Unit.Builders;
using FluentAssertions;

namespace CatalogService.Tests.Unit.Domain;

public class ProductTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsProduct()
    {
        // Arrange & Act
        var product = ProductBuilder.Default().Build();

        // Assert
        product.Id.Should().NotBeEmpty();
        product.Name.Should().NotBeNullOrWhiteSpace();
        product.Price.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Create_WithZeroPrice_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var act = () => ProductBuilder.Default().WithPrice(0m).Build();

        // Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("price");
    }

    [Fact]
    public void Create_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var act = () => ProductBuilder.Default().WithName("").Build();

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithZeroStock_SetsIsAvailableFalse()
    {
        // Arrange & Act
        var product = ProductBuilder.Default().WithStockQuantity(0).Build();

        // Assert
        product.IsAvailable.Should().BeFalse();
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void Create_WithPositiveStock_SetsIsAvailableTrue()
    {
        // Arrange & Act
        var product = ProductBuilder.Default().WithStockQuantity(5).Build();

        // Assert
        product.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void AdjustStock_ToZero_SetsIsAvailableFalse()
    {
        // Arrange
        var product = ProductBuilder.Default().WithStockQuantity(10).Build();

        // Act
        product.AdjustStock(0);

        // Assert
        product.IsAvailable.Should().BeFalse();
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void AdjustStock_ToPositive_SetsIsAvailableTrue()
    {
        // Arrange
        var product = ProductBuilder.Default().WithStockQuantity(0).Build();

        // Act
        product.AdjustStock(5);

        // Assert
        product.IsAvailable.Should().BeTrue();
        product.StockQuantity.Should().Be(5);
    }

    [Fact]
    public void UpdateDetails_WithValidInputs_UpdatesFields()
    {
        // Arrange
        var product = ProductBuilder.Default().Build();

        // Act
        product.UpdateDetails("New Name", "New Desc", 49.99m);

        // Assert
        product.Name.Should().Be("New Name");
        product.Description.Should().Be("New Desc");
        product.Price.Should().Be(49.99m);
    }

    [Fact]
    public void UpdateDetails_SetsUpdatedAt()
    {
        // Arrange
        var product = ProductBuilder.Default().Build();
        var before = DateTime.UtcNow;

        // Act
        product.UpdateDetails("Updated", "Desc", 10m);

        // Assert
        product.UpdatedAt.Should().NotBeNull();
        product.UpdatedAt!.Value.Should().BeOnOrAfter(before);
    }
}
