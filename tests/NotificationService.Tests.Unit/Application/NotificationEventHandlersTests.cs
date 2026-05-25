using AwesomeAssertions;
using NextAurora.Contracts.Events;
using NotificationService.Features;

namespace NotificationService.Tests.Unit.Application;

public class NotificationEventHandlersTests
{
    [Fact]
    public void Handle_OrderPlaced_BuildsOrderReceivedRequestForBuyer()
    {
        // ARRANGE — NotificationService is the saga's "tell the buyer what happened"
        // sidecar. Each event type maps to a templated notification. OrderPlacedEvent
        // → "Order Received" email to the buyer. Like other Wolverine cascading-message
        // handlers in this repo, this is a static pure-function translator — the event
        // shape maps to a SendNotificationRequest. The HANDLER for that request does
        // the actual I/O.
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

        // ACT — Pure function: same input → same output.
        var result = NotificationEventHandlers.Handle(@event);

        // ASSERT — Four invariants:
        //  1) RecipientId is the BUYER (not the order, not the seller) — defends against
        //     a refactor that confuses recipient identity.
        //  2) Subject is the canonical string (UIs/email clients may filter on it).
        //  3) RecipientEmail contains the buyer's id (stub recipient-resolution today —
        //     real address lookup is a deferred capability; placeholder ensures the path
        //     is wired up).
        //  4) Body contains the orderId so the buyer can correlate "which order is this
        //     email about."
        result.RecipientId.Should().Be(buyerId);
        result.Subject.Should().Be("Order Received");
        result.RecipientEmail.Should().Contain(buyerId.ToString("N"));
        result.Body.Should().Contain(orderId.ToString());
    }

    [Fact]
    public void Handle_PaymentFailed_IncludesReasonInBody()
    {
        // ARRANGE — PaymentFailedEvent carries the gateway's raw error (e.g. "card_declined"
        // or a free-text reason). In the buyer-facing notification we DO include the
        // reason for clarity — but the *raw* event reason is also logged server-side for
        // audit. Future enhancement: translate machine codes to friendly strings before
        // the email goes out (e.g. "card_declined" → "Your card was declined"). Today
        // we pass through verbatim.
        var @event = new PaymentFailedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            BuyerId = Guid.NewGuid(),
            Reason = "card_declined",
            FailedAt = DateTime.UtcNow
        };

        // ACT — Call the static translator.
        var result = NotificationEventHandlers.Handle(@event);

        // ASSERT — Two invariants:
        //  1) Subject is the canonical string.
        //  2) Body contains the failure reason verbatim.
        result.Subject.Should().Be("Payment Failed");
        result.Body.Should().Contain("card_declined");
    }

    [Fact]
    public void Handle_ShipmentDispatched_IncludesCarrierAndTrackingNumber()
    {
        // ARRANGE — "Your order has shipped" email. The buyer needs the tracking number
        // and carrier to follow up on the package. Without these in the body, the email
        // is useless and they'll hit support.
        var orderId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };

        // ACT — Call the static translator.
        var result = NotificationEventHandlers.Handle(@event);

        // ASSERT — Three invariants:
        //  1) RecipientId is the ORDER id (because the event doesn't carry BuyerId; the
        //     recipient resolver chases that up downstream). This is a known weakness —
        //     ShipmentDispatchedEvent should carry BuyerId; tracked in Open Issues.
        //  2) Subject is canonical.
        //  3) Body contains both carrier and tracking number — the two pieces of data
        //     the buyer needs to track the package externally.
        result.RecipientId.Should().Be(orderId);
        result.Subject.Should().Be("Order Shipped");
        result.Body.Should().Contain("FedEx").And.Contain("NVC-ABC123");
    }
}
