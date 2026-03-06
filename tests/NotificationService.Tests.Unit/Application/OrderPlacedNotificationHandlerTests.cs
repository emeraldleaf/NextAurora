using FluentAssertions;
using MediatR;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;
using NotificationService.Application.Interfaces;

namespace NotificationService.Tests.Unit.Application;

public class OrderPlacedNotificationHandlerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IRecipientResolver _recipientResolver = Substitute.For<IRecipientResolver>();
    private readonly OrderPlacedNotificationHandler _sut;

    public OrderPlacedNotificationHandlerTests()
    {
        _sut = new OrderPlacedNotificationHandler(_mediator, _recipientResolver);
    }

    [Fact]
    public async Task Handle_SendsNotificationWithValidEmail()
    {
        // Arrange
        var buyerId = Guid.NewGuid();
        var notification = new OrderPlacedNotification(new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        });
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => !string.IsNullOrEmpty(r.RecipientEmail)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SendsNotificationForOrderPlaced()
    {
        // Arrange
        var buyerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notification = new OrderPlacedNotification(new OrderPlacedEvent
        {
            OrderId = orderId,
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        });
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.Subject == "Order Received"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenRecipientNotResolved_DoesNotSendNotification()
    {
        // Arrange
        var buyerId = Guid.NewGuid();
        var notification = new OrderPlacedNotification(new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        });
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns((RecipientInfo?)null);

        // Act
        await _sut.Handle(notification, CancellationToken.None);

        // Assert
        await _mediator.DidNotReceive().Send(Arg.Any<SendNotificationRequest>(), Arg.Any<CancellationToken>());
    }
}
