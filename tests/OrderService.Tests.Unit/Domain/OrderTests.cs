using FluentAssertions;
using OrderService.Domain.Entities;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Domain;

public class OrderTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsOrderWithPlacedStatus()
    {
        var order = OrderBuilder.Default().Build();

        order.Status.Should().Be(OrderStatus.Placed);
        order.Id.Should().NotBeEmpty();
        order.BuyerId.Should().NotBeEmpty();
        order.Lines.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithEmptyBuyerId_ThrowsArgumentException()
    {
        var act = () => OrderBuilder.Default().WithBuyerId(Guid.Empty).Build();

        act.Should().Throw<ArgumentException>().WithParameterName("buyerId");
    }

    [Fact]
    public void Create_WithEmptyLines_ThrowsArgumentException()
    {
        var act = () => OrderBuilder.Default().WithLines([]).Build();

        act.Should().Throw<ArgumentException>().WithParameterName("lines");
    }

    [Fact]
    public void Create_WithNullCurrency_ThrowsArgumentException()
    {
        var act = () => OrderBuilder.Default().WithCurrency(null!).Build();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_CalculatesTotalAmount_FromLines()
    {
        var lines = new List<OrderLine>
        {
            OrderLineBuilder.Default().WithQuantity(2).WithUnitPrice(10m).Build(),
            OrderLineBuilder.Default().WithQuantity(3).WithUnitPrice(5m).Build()
        };

        var order = OrderBuilder.Default().WithLines(lines).Build();

        order.TotalAmount.Should().Be(35m);
    }

    [Fact]
    public void Create_SetsPlacedAtToUtcNow()
    {
        var before = DateTime.UtcNow;
        var order = OrderBuilder.Default().Build();
        var after = DateTime.UtcNow;

        order.PlacedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkAsPaid_WhenPlaced_SetsStatusToPaid()
    {
        var order = OrderBuilder.Default().Build();

        order.MarkAsPaid();

        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsPaid_WhenNotPlaced_ThrowsInvalidOperationException()
    {
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        var act = () => order.MarkAsPaid();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsShipped_WhenPaid_SetsStatusToShipped()
    {
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        order.MarkAsShipped();

        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsShipped_WhenNotPaid_ThrowsInvalidOperationException()
    {
        var order = OrderBuilder.Default().Build();

        var act = () => order.MarkAsShipped();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_WhenPlaced_SetsStatusToCancelled()
    {
        var order = OrderBuilder.Default().Build();

        order.Cancel();

        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenPaid_SetsStatusToCancelled()
    {
        // NOTE: Cancelling a paid order does not trigger a refund — this is a known gap.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        order.Cancel();

        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenShipped_ThrowsInvalidOperationException()
    {
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        order.MarkAsShipped();

        var act = () => order.Cancel();

        act.Should().Throw<InvalidOperationException>();
    }

    // NOTE: Cancel_WhenDelivered is not testable — no MarkAsDelivered() method exists.
    // The Delivered enum value is unreachable. When MarkAsDelivered() is added, add a test here.

    [Fact]
    public void Lines_ReturnsReadOnlyCollection()
    {
        var order = OrderBuilder.Default().Build();

        order.Lines.Should().BeAssignableTo<IReadOnlyList<OrderLine>>();
    }
}
