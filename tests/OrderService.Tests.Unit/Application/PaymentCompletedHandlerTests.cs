using AwesomeAssertions;
using NextAurora.Contracts.Events;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class PaymentCompletedHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly PaymentCompletedHandler _sut;

    public PaymentCompletedHandlerTests()
    {
        _sut = new PaymentCompletedHandler(_repository);
    }

    private static PaymentCompletedEvent EventFor(Guid orderId) => new()
    {
        OrderId = orderId,
        PaymentId = Guid.NewGuid(),
        Amount = 10m,
        Provider = "Stripe",
        CompletedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Handle_WhenOrderExists_MarksOrderAsPaid()
    {
        // ARRANGE — Fresh order in Placed (the only state from which MarkAsPaid is legal).
        // The PaymentCompletedEvent arrives via Wolverine after PaymentService captures
        // the charge — this is the saga's Placed → Paid transition.
        var order = OrderBuilder.Default().Build();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT — Run the handler against the event.
        await _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) The aggregate transitioned to Paid (status guard inside Order.MarkAsPaid
        //     enforces this; we're verifying the call landed and the state moved).
        //  2) UpdateAsync was called — without persistence the in-memory mutation is lost.
        order.Status.Should().Be(OrderStatus.Paid);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutError()
    {
        // ARRANGE — Service Bus is at-least-once. The event can arrive after the order
        // has been deleted, or before its row is visible to this service's read replica.
        // The handler must tolerate this — a NO-OP, not a throw. A throw would land the
        // message on the DLQ and require operator attention for a benign race.
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Wrap so AwesomeAssertions can confirm no exception is thrown.
        var act = () => _sut.HandleAsync(EventFor(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) No exception (the handler short-circuits cleanly on null).
        //  2) No UpdateAsync call (nothing to update; preserves observability accuracy —
        //     we didn't pretend to do work we didn't do).
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderAlreadyPaid_IsIdempotent()
    {
        // ARRANGE — Same PaymentCompletedEvent arrives a second time (Service Bus
        // redelivery). The order is already Paid — MarkAsPaid would throw if called.
        // The handler-level status guard short-circuits BEFORE the domain method so the
        // event is consumed cleanly (no DLQ).
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT — Wrap so AwesomeAssertions can confirm no exception is thrown.
        var act = () => _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) No exception (the handler short-circuits cleanly, not via the domain throw).
        //  2) No UpdateAsync call — proves we short-circuited BEFORE mutating. Without
        //     this, a redelivered event would re-run UpdateAsync with the same state and
        //     pollute observability ("why is this row being saved every retry?").
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
