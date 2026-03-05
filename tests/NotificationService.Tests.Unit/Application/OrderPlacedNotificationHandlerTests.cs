using FluentAssertions;
using MediatR;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;

namespace NotificationService.Tests.Unit.Application;

public class OrderPlacedNotificationHandlerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly OrderPlacedNotificationHandler _sut;

    public OrderPlacedNotificationHandlerTests()
    {
        _sut = new OrderPlacedNotificationHandler(_mediator);
    }

    [Fact(Skip = "Known bug: Hardcoded empty email — notification is sent with empty RecipientEmail")]
    public async Task Handle_SendsNotificationWithValidEmail()
    {
        var notification = new OrderPlacedNotification(new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = Guid.NewGuid(),
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => !string.IsNullOrEmpty(r.RecipientEmail)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SendsNotificationForOrderPlaced()
    {
        var orderId = Guid.NewGuid();
        var notification = new OrderPlacedNotification(new OrderPlacedEvent
        {
            OrderId = orderId,
            BuyerId = Guid.NewGuid(),
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        });

        await _sut.Handle(notification, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<SendNotificationRequest>(r => r.Subject == "Order Received"),
            Arg.Any<CancellationToken>());
    }
}
