using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;
using NotificationService.Application.Interfaces;

namespace NotificationService.Tests.Unit.Application;

public class OrderPlacedNotificationHandlerTests
{
    private readonly IRecipientResolver _recipientResolver = Substitute.For<IRecipientResolver>();
    private readonly OrderPlacedNotificationHandler _sut;

    public OrderPlacedNotificationHandlerTests()
    {
        _sut = new OrderPlacedNotificationHandler(_recipientResolver);
    }

    [Fact]
    public async Task Handle_WhenRecipientResolved_ReturnsSendNotificationRequestWithEmail()
    {
        var buyerId = Guid.NewGuid();
        var @event = new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        };
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RecipientEmail.Should().Be("buyer@test.com");
    }

    [Fact]
    public async Task Handle_WhenRecipientResolved_ReturnsSendNotificationRequestWithOrderReceivedSubject()
    {
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
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Subject.Should().Be("Order Received");
    }

    [Fact]
    public async Task Handle_WhenRecipientNotResolved_ReturnsNull()
    {
        var buyerId = Guid.NewGuid();
        var @event = new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 100m,
            Currency = "USD",
            Lines = []
        };
        _recipientResolver.ResolveByBuyerIdAsync(buyerId, Arg.Any<CancellationToken>())
            .Returns((RecipientInfo?)null);

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().BeNull();
    }
}

