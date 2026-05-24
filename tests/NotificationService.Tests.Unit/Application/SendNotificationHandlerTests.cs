using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NotificationService.Features;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
        // ARRANGE — Happy-path delivery. The handler delegates to INotificationSender
        // (a port substituted today by NSubstitute and in production by the SMTP/SMS
        // adapter). The handler validates inputs and forwards; the sender does the I/O.
        var request = new SendNotificationRequest(
            Guid.NewGuid(), "user@test.com", "Subject", "Body", "Email");

        // ACT
        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) No exception (a clean send).
        //  2) Sender called exactly once with the request's fields — proves the
        //     translation from SendNotificationRequest to the port arguments is correct.
        await act.Should().NotThrowAsync();
        await _sender.Received(1).SendAsync("user@test.com", "Subject", "Body", "Email", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSenderThrows_RethrowsForRetry()
    {
        // ARRANGE — Transient sender failure (SMTP down, rate-limited, etc.). The handler
        // MUST let the exception propagate — Wolverine's retry policy + DLQ are what give
        // us reliability, not in-handler catch-and-ignore. Catching here would silently
        // drop notifications.
        var request = new SendNotificationRequest(
            Guid.NewGuid(), "user@test.com", "Subject", "Body", "Email");
        _sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        // ACT
        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        // ASSERT — Exception propagates UNCHANGED (no wrapping, no swallowing).
        // Wolverine's middleware chain will pick it up and apply the retry policy.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SMTP error");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task Handle_WhenEmailMalformed_Throws(string badEmail)
    {
        // ARRANGE — Input validation runs before the sender so we don't bother SMTP with
        // garbage. Three malformed cases: empty, whitespace, not-an-email-at-all. This
        // is a Theory so all three run as separate test cases — easier to see which one
        // failed if the validator regresses.
        var request = new SendNotificationRequest(
            Guid.NewGuid(), badEmail, "Subject", "Body", "Email");

        // ACT
        var act = () => _sut.HandleAsync(request, CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) ArgumentException is thrown — caller knows their input was malformed.
        //  2) Sender NEVER called — without this, we'd waste SMTP capacity on bad input
        //     and pollute the sender's metrics with guaranteed-failure attempts.
        await act.Should().ThrowAsync<ArgumentException>();
        await _sender.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
