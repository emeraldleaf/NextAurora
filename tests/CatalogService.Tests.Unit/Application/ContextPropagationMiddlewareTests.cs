using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NextAurora.ServiceDefaults.Messaging;
using NSubstitute;
using Wolverine;

namespace CatalogService.Tests.Unit.Application;

public class ContextPropagationMiddlewareTests
{
    private readonly ILogger<ContextPropagationMiddleware> _logger =
        Substitute.For<ILogger<ContextPropagationMiddleware>>();

    [Fact]
    public void Before_WhenEnvelopeHasAllHeaders_RestoresAllContextToActivityBaggage()
    {
        var envelope = new Envelope();
        envelope.Headers["X-Correlation-Id"] = "corr-123";
        envelope.Headers["X-User-Id"] = "user-456";
        envelope.Headers["X-Session-Id"] = "sess-789";

        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var sut = new ContextPropagationMiddleware(_logger);
            sut.Before(envelope);

            Activity.Current?.GetBaggageItem("user.id").Should().Be("user-456");
            Activity.Current?.GetBaggageItem("session.id").Should().Be("sess-789");
            Activity.Current?.GetBaggageItem("correlation.id").Should().Be("corr-123");
        }
    }

    [Fact]
    public void Before_WhenEnvelopeHasNoUserOrSession_ScopeContainsOnlyCorrelationId()
    {
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var envelope = new Envelope();
        envelope.Headers["X-Correlation-Id"] = "corr-only";

        var sut = new ContextPropagationMiddleware(_logger);
        sut.Before(envelope);

        capturedScope.Should().ContainKey("CorrelationId");
        capturedScope.Should().NotContainKey("UserId");
        capturedScope.Should().NotContainKey("SessionId");
    }

    [Fact]
    public void Before_WhenEnvelopeHasUserAndSession_ScopeIncludesBothFields()
    {
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var envelope = new Envelope();
        envelope.Headers["X-User-Id"] = "user-456";
        envelope.Headers["X-Session-Id"] = "sess-789";

        var sut = new ContextPropagationMiddleware(_logger);
        sut.Before(envelope);

        capturedScope.Should().ContainKey("UserId").WhoseValue.Should().Be("user-456");
        capturedScope.Should().ContainKey("SessionId").WhoseValue.Should().Be("sess-789");
    }

    [Fact]
    public void Finally_DisposesScope()
    {
        var scope = Substitute.For<IDisposable>();
        _logger.BeginScope(Arg.Any<Dictionary<string, object?>>()).Returns(scope);

        var envelope = new Envelope();

        var sut = new ContextPropagationMiddleware(_logger);
        sut.Before(envelope);
        sut.Finally();

        scope.Received(1).Dispose();
    }
}

