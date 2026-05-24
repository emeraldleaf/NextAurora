using AwesomeAssertions;
using OrderService.Domain;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Domain;

public class OrderTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsOrderWithPlacedStatus()
    {
        // ARRANGE — Nothing to set up: the OrderBuilder produces a valid order with a
        // single line by default. Every newly created order must enter the saga in the
        // Placed state — it's the saga's start node and the only state from which
        // payment-completed / payment-failed transitions are legal.

        // ACT — Run the factory through the builder.
        var order = OrderBuilder.Default().Build();

        // ASSERT — Four invariants:
        //  1) Status is Placed — the saga's entry state.
        //  2) Id is generated server-side (never empty) — clients never supply an ID.
        //  3) BuyerId carries through — the audit trail needs it from the start.
        //  4) Lines are non-empty — the factory enforces "at least one line".
        order.Status.Should().Be(OrderStatus.Placed);
        order.Id.Should().NotBeEmpty();
        order.BuyerId.Should().NotBeEmpty();
        order.Lines.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithEmptyBuyerId_ThrowsArgumentException()
    {
        // ARRANGE — Guid.Empty is the default value of `Guid` in C#. Without an explicit
        // check, a caller forgetting to set BuyerId would silently create an order belonging
        // to no one — the factory must reject this up front.

        // ACT
        var act = () => OrderBuilder.Default().WithBuyerId(Guid.Empty).Build();

        // ASSERT — Exception names the parameter so callers can fix the right field.
        act.Should().Throw<ArgumentException>().WithParameterName("buyerId");
    }

    [Fact]
    public void Create_WithEmptyLines_ThrowsArgumentException()
    {
        // ARRANGE — An order with no lines has no total, nothing to ship, and no reason to
        // exist. The factory enforces "at least one line" so the rule lives in one place
        // rather than being scattered across endpoint validators.

        // ACT
        var act = () => OrderBuilder.Default().WithLines([]).Build();

        // ASSERT
        act.Should().Throw<ArgumentException>().WithParameterName("lines");
    }

    [Fact]
    public void Create_WithNullCurrency_ThrowsArgumentException()
    {
        // ARRANGE — Currency is required for downstream payment processing. Null/whitespace
        // is caught at the factory via ArgumentException.ThrowIfNullOrWhiteSpace.

        // ACT — Pass null! to bypass the C# nullable check (this models a real caller bug,
        // not an intended API).
        var act = () => OrderBuilder.Default().WithCurrency(null!).Build();

        // ASSERT
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_CalculatesTotalAmount_FromLines()
    {
        // ARRANGE — TotalAmount is derived server-side from the lines, never trusted from
        // the client (CLAUDE.md "Order.Create" comment). If we accepted a client-submitted
        // total, anyone could place a $999 order for $0.01. Two lines: 2×$10 + 3×$5 = $35.
        var lines = new List<OrderLine>
        {
            OrderLineBuilder.Default().WithQuantity(2).WithUnitPrice(10m).Build(),
            OrderLineBuilder.Default().WithQuantity(3).WithUnitPrice(5m).Build()
        };

        // ACT
        var order = OrderBuilder.Default().WithLines(lines).Build();

        // ASSERT — The factory's sum matches the expected total. A future refactor that
        // accidentally trusts a `total` argument from the caller would fail this test.
        order.TotalAmount.Should().Be(35m);
    }

    [Fact]
    public void Create_SetsPlacedAtToUtcNow()
    {
        // ARRANGE — PlacedAt is the only timestamp set at factory time; later transitions
        // (PaidAt, ShippedAt) come from their respective transition methods. We bracket
        // the call with `before`/`after` to avoid flakiness from system-clock granularity.
        var before = DateTime.UtcNow;

        // ACT
        var order = OrderBuilder.Default().Build();
        var after = DateTime.UtcNow;

        // ASSERT — PlacedAt is within [before, after]. UTC — never local time, so logs
        // and traces stay coherent across regions.
        order.PlacedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkAsPaid_WhenPlaced_SetsStatusToPaid()
    {
        // ARRANGE — Happy-path saga transition: PaymentCompletedHandler calls MarkAsPaid
        // after Service Bus delivers PaymentCompletedEvent.
        var order = OrderBuilder.Default().Build();

        // ACT
        order.MarkAsPaid();

        // ASSERT — Two invariants:
        //  1) Status transitioned to Paid.
        //  2) PaidAt is now non-null (read by audit/admin views).
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsPaid_WhenNotPlaced_ThrowsInvalidOperationException()
    {
        // ARRANGE — Calling MarkAsPaid on an order that's already Paid (or any non-Placed
        // state). The status guard is what makes the handler idempotent under Service Bus
        // at-least-once delivery — a redelivered PaymentCompletedEvent must NOT corrupt
        // state. The HANDLER catches the throw and treats it as a no-op (see
        // PaymentCompletedHandlerTests); here we verify the DOMAIN-level guard exists.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // ACT
        var act = () => order.MarkAsPaid();

        // ASSERT
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsShipped_WhenPaid_SetsStatusToShipped()
    {
        // ARRANGE — Happy-path transition: ShipmentDispatchedHandler calls MarkAsShipped
        // after the shipping saga commits the dispatch. The order must be Paid first
        // (we can't ship something we haven't been paid for).
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // ACT
        order.MarkAsShipped();

        // ASSERT — Two invariants:
        //  1) Status is now Shipped.
        //  2) ShippedAt is non-null — needed for buyer-facing "shipped on" timestamps.
        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsShipped_WhenNotPaid_ThrowsInvalidOperationException()
    {
        // ARRANGE — Order is still in Placed (payment hasn't completed yet, or has failed).
        // We must not ship a Placed order — the saga's invariant is "Paid before Shipped".
        // This test guards against a future bug where someone bypasses the status check.
        var order = OrderBuilder.Default().Build();

        // ACT
        var act = () => order.MarkAsShipped();

        // ASSERT
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_WhenPlaced_SetsStatusToCancelled()
    {
        // ARRANGE — A buyer cancels their order before payment completes. The simplest
        // cancellation path: no compensation needed (no payment captured, no stock to
        // release yet — that happens in the payment service).
        var order = OrderBuilder.Default().Build();

        // ACT
        order.Cancel();

        // ASSERT
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenPaid_SetsStatusToCancelled()
    {
        // ARRANGE — Cancelling a paid order. Known gap: this currently does NOT trigger
        // a refund — the domain accepts the transition but no compensating action runs.
        // Future work (see STATUS.md "Saga compensation") will wire a RefundRequiredEvent.
        // Locking in current behaviour so the gap is testable + obvious.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();

        // ACT
        order.Cancel();

        // ASSERT
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Cancel_WhenShipped_ThrowsInvalidOperationException()
    {
        // ARRANGE — Once the package has left the warehouse, cancellation isn't a state
        // transition any more — it's a return/refund process the buyer initiates after
        // delivery. The domain enforces this boundary up front.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        order.MarkAsShipped();

        // ACT
        var act = () => order.Cancel();

        // ASSERT
        act.Should().Throw<InvalidOperationException>();
    }

    // NOTE: Cancel_WhenDelivered is not testable — no MarkAsDelivered() method exists.
    // The Delivered enum value is unreachable. When MarkAsDelivered() is added, add a test here.

    [Fact]
    public void Lines_ReturnsReadOnlyCollection()
    {
        // ARRANGE — DDD encapsulation rule: aggregates never expose mutable child
        // collections. If `Lines` returned `List<OrderLine>`, callers could call
        // `order.Lines.Add(...)` and bypass our "TotalAmount equals sum of lines"
        // invariant. Exposing IReadOnlyList<T> makes mutation a compile error.
        // This is a structural test — it would fail at compile time too if someone
        // changed the return type, but the runtime assertion documents intent.

        // ACT
        var order = OrderBuilder.Default().Build();

        // ASSERT
        order.Lines.Should().BeAssignableTo<IReadOnlyList<OrderLine>>();
    }
}
