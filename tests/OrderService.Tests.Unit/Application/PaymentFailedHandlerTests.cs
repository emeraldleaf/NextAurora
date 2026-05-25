using AwesomeAssertions;
using NextAurora.Contracts.Events;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class PaymentFailedHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly PaymentFailedHandler _sut;

    public PaymentFailedHandlerTests()
    {
        _sut = new PaymentFailedHandler(_repository);
    }

    private static PaymentFailedEvent EventFor(Guid orderId) => new()
    {
        PaymentId = Guid.NewGuid(),
        OrderId = orderId,
        BuyerId = Guid.NewGuid(),
        Reason = "Card declined",
        FailedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Handle_WhenOrderInPlaced_TransitionsToPaymentFailedAndSaves()
    {
        // ARRANGE — Fresh order is in OrderStatus.Placed (the only state from which
        // MarkAsPaymentFailed is legal). The PaymentFailedEvent arrives via Wolverine
        // after PaymentService rejects the charge.
        var order = OrderBuilder.Default().Build();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT — Run the handler against the event.
        await _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) The aggregate transitioned to PaymentFailed (terminal state — no compensation
        //     here; the buyer places a new order if they want to retry).
        //  2) The repository's UpdateAsync was called so the new state actually persists.
        //     Without this, the in-memory mutation would be lost.
        order.Status.Should().Be(OrderStatus.PaymentFailed);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutErrorAndDoesNotSave()
    {
        // ARRANGE — Service Bus is at-least-once: the event can arrive after the order
        // has been deleted or before its row is visible to this service's read replica.
        // The handler must be tolerant — a no-op, not a throw (a throw would land the
        // message on the DLQ and demand operator attention for a benign race).
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Wrap so AwesomeAssertions can confirm no exception is thrown.
        var act = () => _sut.HandleAsync(EventFor(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Two invariants, both expressing the idempotency contract:
        //  1) No exception — a throw would land the message on the DLQ and demand
        //     operator attention for a benign at-least-once race (order deleted, read
        //     replica lag). The handler treats "order missing" as the saga having moved
        //     on, not as an error.
        //  2) No UpdateAsync call — proves the absent order didn't trigger a phantom
        //     persistence attempt (e.g. saving a freshly constructed default Order would
        //     silently corrupt state). The branch must short-circuit cleanly.
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderAlreadyPaid_IsIdempotentAndDoesNotSave()
    {
        // ARRANGE — A late-arriving PaymentFailedEvent against an order that already
        // moved on to Paid (the saga's expected path) must NOT undo the payment. Same
        // idempotency contract as PaymentCompletedHandler: status-guard at the handler
        // AND the domain (Order.MarkAsPaymentFailed throws if not in Placed). Without
        // the handler-level guard, calling the domain method would throw and the event
        // would be DLQ'd — undesirable for a benign late delivery.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        var statusBefore = order.Status;
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT — Wrap so AwesomeAssertions can confirm no exception is thrown.
        var act = () => _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) No exception (the handler short-circuits cleanly).
        //  2) Status unchanged — Paid stays Paid.
        //  3) No save call — proves we short-circuited BEFORE mutating, not after.
        await act.Should().NotThrowAsync();
        order.Status.Should().Be(statusBefore);
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
