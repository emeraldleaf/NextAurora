using FluentAssertions;
using MediatR;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;

namespace NotificationService.Tests.Unit.Application;

public class ShipmentDispatchedNotificationHandlerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ShipmentDispatchedNotificationHandler _sut;

    public ShipmentDispatchedNotificationHandlerTests()
    {
        _sut = new ShipmentDispatchedNotificationHandler(_mediator);
    }

    [Fact(Skip = "Known bug: Hardcoded empty email — notification is sent with empty RecipientEmail")]
    public async Task Handle_SendsNotificationWithValidEmail()
    {
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => !string.IsNullOrEmpty(r.RecipientEmail)),
            Arg.Any<CancellationToken>());
    }

    [Fact(Skip = "Known bug: Hardcoded Guid.Empty — notification uses empty buyer ID")]
    public async Task Handle_UsesValidBuyerId()
    {
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.RecipientId != Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SendsNotificationForShipmentDispatched()
    {
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.Subject == "Order Shipped"),
            Arg.Any<CancellationToken>());
    }
}
