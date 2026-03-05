using FluentAssertions;
using OrderService.Domain.Entities;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Domain;

public class OrderLineTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsOrderLine()
    {
        var line = OrderLineBuilder.Default().Build();

        line.Id.Should().NotBeEmpty();
        line.ProductId.Should().NotBeEmpty();
        line.Quantity.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Create_WithEmptyProductId_ThrowsArgumentException()
    {
        var act = () => OrderLineBuilder.Default().WithProductId(Guid.Empty).Build();

        act.Should().Throw<ArgumentException>().WithParameterName("productId");
    }

    [Fact]
    public void Create_WithZeroQuantity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => OrderLineBuilder.Default().WithQuantity(0).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quantity");
    }

    [Fact]
    public void Create_WithNegativeQuantity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => OrderLineBuilder.Default().WithQuantity(-1).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quantity");
    }

    [Fact]
    public void Create_WithNegativePrice_ThrowsArgumentOutOfRangeException()
    {
        var act = () => OrderLineBuilder.Default().WithUnitPrice(-1m).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("unitPrice");
    }

    [Fact]
    public void Create_WithZeroPrice_Succeeds()
    {
        // NOTE: Zero price is allowed by ThrowIfNegative (not ThrowIfNegativeOrZero).
        // This may be intentional (free items) or a bug — documenting current behavior.
        var line = OrderLineBuilder.Default().WithUnitPrice(0m).Build();

        line.UnitPrice.Should().Be(0m);
    }
}
