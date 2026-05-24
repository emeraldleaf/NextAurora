using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NextAurora.ServiceDefaults.Messaging;
using NSubstitute;
using Wolverine;

namespace PaymentService.Tests.Unit.Application;

public class ContextPropagationMiddlewareTests
{
    private readonly ILogger<ContextPropagationMiddleware> _logger =
        Substitute.For<ILogger<ContextPropagationMiddleware>>();

    [Fact]
    public void Before_WhenEnvelopeHasAllHeaders_RestoresAllContextToActivityBaggage()
    {
        // ARRANGE — The Wolverine consumer-side counterpart to OutgoingContextMiddleware.
        // When a message arrives, this middleware reads X-Correlation-Id / X-User-Id /
        // X-Session-Id from the envelope and writes them into the current Activity's
        // baggage. From there, the logger scope (also opened by this middleware) picks
        // them up and every log line in the handler carries the originating context.
        // We simulate a full-context envelope as it would arrive from an upstream service.
        var envelope = new Envelope();
        envelope.Headers["X-Correlation-Id"] = "corr-123";
        envelope.Headers["X-User-Id"] = "user-456";
        envelope.Headers["X-Session-Id"] = "sess-789";

        // The middleware reads from Activity.Current; we have to start one so the
        // baggage call has somewhere to write to. In production this Activity comes
        // from Wolverine's OpenTelemetry instrumentation.
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var sut = new ContextPropagationMiddleware(_logger);

            // ACT
            sut.Before(envelope);

            // ASSERT — Three invariants: all three context fields are restored into
            // baggage. If any of these regress, the saga's audit trail breaks at this
            // hop — the handler runs with the wrong (or null) user/session context.
            Activity.Current?.GetBaggageItem("user.id").Should().Be("user-456");
            Activity.Current?.GetBaggageItem("session.id").Should().Be("sess-789");
            Activity.Current?.GetBaggageItem("correlation.id").Should().Be("corr-123");
        }
    }

    [Fact]
    public void Before_WhenEnvelopeHasUserAndSession_ScopeIncludesBothFields()
    {
        // ARRANGE — Capture the dictionary passed to BeginScope so we can introspect it.
        // The logger scope is what makes "every log line in the handler carries UserId
        // and SessionId" actually work — without this scope, structured logs would have
        // a null UserId field even though baggage was set.
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var envelope = new Envelope();
        envelope.Headers["X-User-Id"] = "user-456";
        envelope.Headers["X-Session-Id"] = "sess-789";

        var sut = new ContextPropagationMiddleware(_logger);

        // ACT
        sut.Before(envelope);

        // ASSERT — Both keys present with the right values. The scope dictionary is
        // what ILogger.BeginScope receives and structured-logging sinks (Seq, OTLP,
        // App Insights) flatten into log properties.
        capturedScope.Should().ContainKey("UserId").WhoseValue.Should().Be("user-456");
        capturedScope.Should().ContainKey("SessionId").WhoseValue.Should().Be("sess-789");
    }

    [Fact]
    public void Before_WhenEnvelopeHasNoOptionalHeaders_ScopeOmitsUserAndSessionKeys()
    {
        // ARRANGE — A message from a system origin (BackgroundService sweep, no user
        // context). Envelope has neither X-User-Id nor X-Session-Id. The scope must
        // OMIT these keys entirely — adding them with null/empty values would pollute
        // log queries. CLAUDE.md "Observability": never add null/empty keys to scope
        // dictionaries.
        Dictionary<string, object?>? capturedScope = null;
        _logger.BeginScope(Arg.Do<Dictionary<string, object?>>(d => capturedScope = d))
               .Returns(Substitute.For<IDisposable>());

        var envelope = new Envelope();

        var sut = new ContextPropagationMiddleware(_logger);

        // ACT
        sut.Before(envelope);

        // ASSERT — Neither key is in the scope dictionary. A test that asserted the
        // values were null would miss the bug — `ContainKey("UserId").WhoseValue == null`
        // would pass even though the key shouldn't exist at all. NotContainKey is the
        // correct shape.
        capturedScope.Should().NotContainKey("UserId");
        capturedScope.Should().NotContainKey("SessionId");
    }

    [Fact]
    public void Finally_DisposesScope()
    {
        // ARRANGE — Wolverine's middleware contract: Before opens resources, Finally
        // disposes them after the handler runs (even on exception). If we forget to
        // dispose, the logger scope leaks — subsequent log lines on the same thread
        // would mysteriously carry the previous handler's UserId/SessionId. That's
        // both a privacy issue (cross-request data bleed) and a debugging nightmare.
        var scope = Substitute.For<IDisposable>();
        _logger.BeginScope(Arg.Any<Dictionary<string, object?>>()).Returns(scope);

        var envelope = new Envelope();

        var sut = new ContextPropagationMiddleware(_logger);
        sut.Before(envelope);

        // ACT
        sut.Finally();

        // ASSERT — Dispose called exactly once. Twice would also be wrong (double-dispose
        // on an IDisposable is technically allowed but signals a programming error here).
        scope.Received(1).Dispose();
    }
}
