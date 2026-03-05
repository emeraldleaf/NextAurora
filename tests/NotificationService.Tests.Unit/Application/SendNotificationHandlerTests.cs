using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NotificationService.Application.Commands;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Tests.Unit.Application;

public class SendNotificationHandlerTests
{
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();
    private readonly ILogger<SendNotificationHandler> _logger = Substitute.For<ILogger<SendNotificationHandler>>();
    private readonly SendNotificationHandler _sut;

    public SendNotificationHandlerTests()
    {
        _sut = new SendNotificationHandler(_sender, _logger);
    }

    [Fact]
    public async Task Handle_WhenSenderSucceeds_CompletesWithoutError()
    {
        var request = new SendNotificationRequest(
            Guid.NewGuid(), "user@test.com", "Subject", "Body", "Email");

        var act = () => _sut.Handle(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _sender.Received(1).SendAsync("user@test.com", "Subject", "Body", "Email", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderThrows_DoesNotRethrow()
    {
        var request = new SendNotificationRequest(
            Guid.NewGuid(), "user@test.com", "Subject", "Body", "Email");
        _sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        var act = () => _sut.Handle(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
