using FluentAssertions;
using NextAurora.Contracts.Events;
using ShippingService.Application.Commands;
using ShippingService.Application.EventHandlers;

namespace ShippingService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    [Fact]
    public void Handle_ReturnsCreateShipmentCommandWithCorrectOrderId()
    {
        var orderId = Guid.NewGuid();
        var @event = new PaymentCompletedEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            Amount = 100m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };

        var result = PaymentCompletedHandler.Handle(@event);

        result.Should().BeOfType<CreateShipmentCommand>();
        result.OrderId.Should().Be(orderId);
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

