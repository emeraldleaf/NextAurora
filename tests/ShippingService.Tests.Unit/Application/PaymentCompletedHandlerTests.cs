using AwesomeAssertions;
using NextAurora.Contracts.Events;
using ShippingService.Features;

namespace ShippingService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    [Fact]
    public void Handle_ReturnsCreateShipmentCommandWithCorrectOrderIdAndBuyerId()
    {
        // ARRANGE — ShippingService's saga entry point. Like PaymentService.OrderPlacedHandler,
        // this is a *Wolverine cascading message* — a static method that translates an event
        // into the next command, returned for Wolverine to dispatch. The whole class exists
        // for one reason: CreateShipmentCommand is reachable from two paths (saga + admin
        // endpoint), so we keep one handler that owns the work and a thin event-translator
        // on top. Open/Closed — a third trigger dispatches the same command unchanged.
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

        // ACT — Pure function: same input → same output. No async, no I/O.
        var result = PaymentCompletedHandler.Handle(@event);

        // ASSERT — Three invariants:
        //  1) Result type is correct (Wolverine routes by message type — the wrong type
        //     would simply fail to dispatch with no clear error).
        //  2) OrderId round-trips so the shipment can be attached to the right order.
        //  3) BuyerId round-trips so the Shipment carries it for the IDOR-prevention
        //     check on GET /api/v1/shipments/order/{orderId}. If this regressed, the
        //     buyer-scope check would have nothing to compare against.
        result.Should().BeOfType<CreateShipmentCommand>();
        result.OrderId.Should().Be(orderId);
        result.BuyerId.Should().Be(buyerId);
    }

    [Fact]
    public void Handle_CalledTwiceWithSameEvent_ReturnsTwoDistinctCommandInstances()
    {
        // ARRANGE — Wolverine's cascading-message model expects a NEW command instance
        // per dispatch. If this method ever started returning a cached/static command,
        // mutations in one handler's pipeline could bleed into another (Wolverine
        // middleware can read/write command fields). Two calls with the same event must
        // produce two reference-distinct CreateShipmentCommand records — defends against
        // a refactor that accidentally introduces a static cache.
        var orderId = Guid.NewGuid();
        var @event = new PaymentCompletedEvent
        {
            OrderId = orderId,
            BuyerId = Guid.NewGuid(),
            PaymentId = Guid.NewGuid(),
            Amount = 50m,
            Provider = "Stripe",
            CompletedAt = DateTime.UtcNow
        };

        // ACT — Two independent calls.
        var first = PaymentCompletedHandler.Handle(@event);
        var second = PaymentCompletedHandler.Handle(@event);

        // ASSERT — Three invariants:
        //  1) Both results are non-null.
        //  2) Both carry the same logical OrderId (proves the translator is deterministic
        //     on the inputs — same event → same field values).
        //  3) They are NOT the same reference — distinct allocations. Without this, a
        //     future "optimization" that caches the command would silently break
        //     Wolverine's per-message isolation.
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.OrderId.Should().Be(orderId);
        second.OrderId.Should().Be(orderId);
        ReferenceEquals(first, second).Should().BeFalse(
            "each dispatch must allocate a new command — see ARRANGE comment");
    }
}
