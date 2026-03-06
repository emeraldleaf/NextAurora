using System.Diagnostics;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CatalogService.Application.Behaviors;

namespace CatalogService.Tests.Unit.Application;

public class LoggingBehaviorTests
{
    private readonly ILogger<LoggingBehavior<LoggingBehaviorTestRequest, string>> _logger =
        Substitute.For<ILogger<LoggingBehavior<LoggingBehaviorTestRequest, string>>>();

    [Fact]
    public async Task Handle_WhenActivityBaggageContainsUserAndSession_ScopeIncludesBothFields()
    {
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            activity.SetBaggage("correlation.id", "corr-123");
            activity.SetBaggage("user.id", "user-456");
            activity.SetBaggage("session.id", "sess-789");

            var sut = new LoggingBehavior<LoggingBehaviorTestRequest, string>(_logger);
            await sut.Handle(new LoggingBehaviorTestRequest(), () => Task.FromResult("ok"), CancellationToken.None);
        }

        capturedScope.Should().ContainKey("UserId").WhoseValue.Should().Be("user-456");
        capturedScope.Should().ContainKey("SessionId").WhoseValue.Should().Be("sess-789");
    }

    [Fact]
    public async Task Handle_WhenNoBaggageSet_ScopeOmitsUserAndSessionKeys()
    {
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var sut = new LoggingBehavior<LoggingBehaviorTestRequest, string>(_logger);
            await sut.Handle(new LoggingBehaviorTestRequest(), () => Task.FromResult("ok"), CancellationToken.None);
        }

        capturedScope.Should().NotContainKey("UserId");
        capturedScope.Should().NotContainKey("SessionId");
    }

    [Fact]
    public async Task Handle_AlwaysInvokesDelegateAndReturnsResult()
    {
        _logger.BeginScope(Arg.Any<Dictionary<string, object?>>()).Returns(Substitute.For<IDisposable>());

        var sut = new LoggingBehavior<LoggingBehaviorTestRequest, string>(_logger);
        var result = await sut.Handle(new LoggingBehaviorTestRequest(), () => Task.FromResult("expected"), CancellationToken.None);

        result.Should().Be("expected");
    }
}

public sealed record LoggingBehaviorTestRequest : IRequest<string>;
