using FluentAssertions;
using MediatR;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;
using NotificationService.Application.Interfaces;

namespace NotificationService.Tests.Unit.Application;

public class ShipmentDispatchedNotificationHandlerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IRecipientResolver _recipientResolver = Substitute.For<IRecipientResolver>();
    private readonly ShipmentDispatchedNotificationHandler _sut;

    public ShipmentDispatchedNotificationHandlerTests()
    {
        _sut = new ShipmentDispatchedNotificationHandler(_mediator, _recipientResolver);
    }

    [Fact]
    public async Task Handle_SendsNotificationWithValidEmail()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => !string.IsNullOrEmpty(r.RecipientEmail)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UsesValidBuyerId()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.RecipientId != Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SendsNotificationForShipmentDispatched()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(Guid.NewGuid(), "buyer@test.com"));

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.Subject == "Order Shipped"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRecipientNotResolved_DoesNotSendNotification()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var notification = new ShipmentDispatchedNotification(new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        });
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((RecipientInfo?)null);

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.DidNotReceive().Send(Arg.Any<SendNotificationRequest>(), Arg.Any<CancellationToken>());
    }
}
