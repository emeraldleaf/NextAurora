using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;
using NotificationService.Application.Interfaces;

namespace NotificationService.Tests.Unit.Application;

public class ShipmentDispatchedNotificationHandlerTests
{
    private readonly IRecipientResolver _recipientResolver = Substitute.For<IRecipientResolver>();
    private readonly ShipmentDispatchedNotificationHandler _sut;

    public ShipmentDispatchedNotificationHandlerTests()
    {
        _sut = new ShipmentDispatchedNotificationHandler(_recipientResolver);
    }

    [Fact]
    public async Task Handle_WhenRecipientResolved_ReturnsSendNotificationRequestWithEmail()
    {
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(buyerId, "buyer@test.com"));

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RecipientEmail.Should().Be("buyer@test.com");
    }

    [Fact]
    public async Task Handle_WhenRecipientResolved_ReturnsSendNotificationRequestWithOrderShippedSubject()
    {
        var orderId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(Guid.NewGuid(), "buyer@test.com"));

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Subject.Should().Be("Order Shipped");
    }

    [Fact]
    public async Task Handle_WhenRecipientNotResolved_ReturnsNull()
    {
        var orderId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((RecipientInfo?)null);

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenRecipientResolved_BodyContainsCarrierAndTrackingNumber()
    {
        var orderId = Guid.NewGuid();
        var @event = new ShipmentDispatchedEvent
        {
            ShipmentId = Guid.NewGuid(),
            OrderId = orderId,
            Carrier = "FedEx",
            TrackingNumber = "NVC-ABC123",
            DispatchedAt = DateTime.UtcNow
        };
        _recipientResolver.ResolveByOrderIdAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(new RecipientInfo(Guid.NewGuid(), "buyer@test.com"));

        var result = await _sut.Handle(@event, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Body.Should().Contain("FedEx");
        result.Body.Should().Contain("NVC-ABC123");
    }
}

