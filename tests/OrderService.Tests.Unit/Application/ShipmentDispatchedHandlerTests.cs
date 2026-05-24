using AwesomeAssertions;
using NextAurora.Contracts.Events;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class ShipmentDispatchedHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly ShipmentDispatchedHandler _sut;

    public ShipmentDispatchedHandlerTests()
    {
        _sut = new ShipmentDispatchedHandler(_repository);
    }

    private static ShipmentDispatchedEvent EventFor(Guid orderId) => new()
    {
        OrderId = orderId,
        ShipmentId = Guid.NewGuid(),
        Carrier = "FedEx",
        TrackingNumber = "NVC-123",
        DispatchedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Handle_WhenOrderExists_MarksOrderAsShipped()
    {
        // ARRANGE — Order in Paid (the only state from which MarkAsShipped is legal).
        // ShipmentDispatchedEvent arrives via Wolverine after ShippingService dispatches
        // the package — this is the saga's Paid → Shipped transition.
        var order = OrderBuilder.Default().Build();
        order.MarkAsPaid();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT
        await _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) Status transitioned to Shipped (the domain's MarkAsShipped ran successfully).
        //  2) UpdateAsync was called to persist the transition.
        order.Status.Should().Be(OrderStatus.Shipped);
        await _repository.Received(1).UpdateAsync(order, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsWithoutError()
    {
        // ARRANGE — Late-arriving event for a deleted order. Same Service Bus at-least-once
        // tolerance rule as PaymentCompletedHandler: tolerate, don't throw, don't DLQ.
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        // ACT
        var act = () => _sut.HandleAsync(EventFor(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — No exception. The handler short-circuits silently.
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handle_WhenOrderNotPaid_IsIdempotent()
    {
        // ARRANGE — Order is in Placed (payment somehow hasn't reached Paid yet, OR the
        // ShipmentDispatchedEvent arrived before the PaymentCompletedEvent due to
        // out-of-order delivery — Service Bus doesn't guarantee per-message ordering
        // across different topics). Without the handler-level status guard, calling
        // MarkAsShipped here would throw (domain enforces Paid-before-Shipped). The
        // guard short-circuits cleanly so the event is consumed and Wolverine moves on.
        var order = OrderBuilder.Default().Build();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT
        var act = () => _sut.HandleAsync(EventFor(order.Id), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) No exception (clean short-circuit, not a domain throw).
        //  2) No UpdateAsync call — we don't pretend to do work we didn't do.
        // NOTE: A "stuck Placed forever" scenario is recovered separately by the
        // PaymentRecoveryJob sweeper; that's not this handler's concern.
        await act.Should().NotThrowAsync();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
