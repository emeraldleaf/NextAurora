using AwesomeAssertions;
using NextAurora.Contracts.Events;
using ShippingService.Features;

namespace ShippingService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    [Fact]
    public void Handle_ReturnsCreateShipmentCommandWithCorrectOrderIdAndBuyerId()
    {
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var @event = new PaymentCompletedEvent
        {
            OrderId = orderId,
            BuyerId = buyerId,
            PaymentId = Guid.NewGuid(),
            Amount = 100m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };

        var result = PaymentCompletedHandler.Handle(@event);

        result.Should().BeOfType<CreateShipmentCommand>();
        result.OrderId.Should().Be(orderId);
        result.BuyerId.Should().Be(buyerId);
    }

    [Fact]
    public void Handle_AlwaysReturnsNewCommand()
    {
        var orderId = Guid.NewGuid();
        var @event = new PaymentCompletedEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            Amount = 50m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };

        var result = PaymentCompletedHandler.Handle(@event);

        result.Should().NotBeNull();
        result.OrderId.Should().Be(orderId);
    }
}

