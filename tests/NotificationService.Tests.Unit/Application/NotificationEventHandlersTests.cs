using AwesomeAssertions;
using NextAurora.Contracts.Events;
using NotificationService.Features;

namespace NotificationService.Tests.Unit.Application;

public class NotificationEventHandlersTests
{
    [Fact]
    public void Handle_OrderPlaced_BuildsOrderReceivedRequestForBuyer()
    {
        var buyerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var @event = new OrderPlacedEvent
        {
            OrderId = orderId,
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        };

        var result = NotificationEventHandlers.Handle(@event);

        result.RecipientId.Should().Be(buyerId);
        result.Subject.Should().Be("Order Received");
        result.RecipientEmail.Should().Contain(buyerId.ToString("N"));
        result.Body.Should().Contain(orderId.ToString());
    }

    [Fact]
    public void Handle_PaymentFailed_IncludesReasonInBody()
    {
        var @event = new PaymentFailedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            BuyerId = Guid.NewGuid(),
            Reason = "card_declined",
            FailedAt = DateTime.UtcNow
        };

        var result = NotificationEventHandlers.Handle(@event);

        result.Subject.Should().Be("Payment Failed");
        result.Body.Should().Contain("card_declined");
    }

    [Fact]
    public void Handle_ShipmentDispatched_IncludesCarrierAndTrackingNumber()
    {
        var orderId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };

        var result = NotificationEventHandlers.Handle(@event);

        result.RecipientId.Should().Be(orderId);
        result.Subject.Should().Be("Order Shipped");
        result.Body.Should().Contain("FedEx").And.Contain("NVC-ABC123");
    }
}
