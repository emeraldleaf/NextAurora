using FluentAssertions;
using OrderService.Domain.Entities;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Domain;

public class OrderTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsOrderWithPlacedStatus()
    {
        // Act
        var order = OrderBuilder.Default().Build();

        // Assert
        order.Status.Should().Be(OrderStatus.Placed);
        order.Id.Should().NotBeEmpty();
        order.BuyerId.Should().NotBeEmpty();
        order.Lines.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithEmptyBuyerId_ThrowsArgumentException()
    {
        // Act
        var act = () => OrderBuilder.Default().WithBuyerId(Guid.Empty).Build();

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName("buyerId");
    }

    [Fact]
    public void Create_WithEmptyLines_ThrowsArgumentException()
    {
        // Act
        var act = () => OrderBuilder.Default().WithLines([]).Build();

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName("lines");
    }

    [Fact]
    public void Create_WithNullCurrency_ThrowsArgumentException()
    {
        // Act
        var act = () => OrderBuilder.Default().WithCurrency(null!).Build();

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_CalculatesTotalAmount_FromLines()
    {
        // Arrange
        var lines = new List<OrderLine>
        {
            OrderLineBuilder.Default().WithQuantity(2).WithUnitPrice(10m).Build(),
            OrderLineBuilder.Default().WithQuantity(3).WithUnitPrice(5m).Build()
        };

        // Act
        var order = OrderBuilder.Default().WithLines(lines).Build();

        // Assert
        order.TotalAmount.Should().Be(35m);
    }

    [Fact]
    public void Create_SetsPlacedAtToUtcNow()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var order = OrderBuilder.Default().Build();
        var after = DateTime.UtcNow;

        // Assert
        order.PlacedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkAsPaid_WhenPlaced_SetsStatusToPaid()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();

        // Act
        order.MarkAsPaid();

        // Assert
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsPaid_WhenNotPlaced_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // Act
        var act = () => order.MarkAsPaid();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsShipped_WhenPaid_SetsStatusToShipped()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // Act
        order.MarkAsShipped();

        // Assert
        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsShipped_WhenNotPaid_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();

        // Act
        var act = () => order.MarkAsShipped();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_WhenPlaced_SetsStatusToCancelled()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();

        // Act
        order.Cancel();

        // Assert
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenPaid_SetsStatusToCancelled()
    {
        // Arrange — cancelling a paid order does not trigger a refund (known gap)
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // Act
        order.Cancel();

        // Assert
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenShipped_ThrowsInvalidOperationException()
    {
        // Arrange
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        order.MarkAsShipped();

        // Act
        var act = () => order.Cancel();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    // NOTE: Cancel_WhenDelivered is not testable — no MarkAsDelivered() method exists.
    // The Delivered enum value is unreachable. When MarkAsDelivered() is added, add a test here.

    [Fact]
    public void Lines_ReturnsReadOnlyCollection()
    {
        // Act
        var order = OrderBuilder.Default().Build();

        // Assert
        order.Lines.Should().BeAssignableTo<IReadOnlyList<OrderLine>>();
    }
}
