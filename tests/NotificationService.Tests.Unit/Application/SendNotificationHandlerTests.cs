using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NotificationService.Features;

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

        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _sender.Received(1).SendAsync("user@test.com", "Subject", "Body", "Email", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderThrows_RethrowsForRetry()
    {
        var request = new SendNotificationRequest(
            Guid.NewGuid(), "user@test.com", "Subject", "Body", "Email");
        _sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        // Exceptions must propagate so Wolverine can retry or dead-letter.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SMTP error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task Handle_WhenEmailMalformed_Throws(string badEmail)
    {
        var request = new SendNotificationRequest(
            Guid.NewGuid(), badEmail, "Subject", "Body", "Email");

        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await _sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
