using System.Diagnostics;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NextAurora.ServiceDefaults.Middleware;
using NSubstitute;

namespace OrderService.Tests.Unit.Application;

public class CorrelationIdMiddlewareTests
{
    private readonly ILogger<CorrelationIdMiddleware> _logger = Substitute.For<ILogger<CorrelationIdMiddleware>>();

    private static DefaultHttpContext BuildContext(
        ClaimsPrincipal? user = null, string? sessionId = null, string? correlationId = null)
    {
        var ctx = new DefaultHttpContext();
        if (user is not null) ctx.User = user;
        if (sessionId is not null) ctx.Request.Headers["X-Session-Id"] = new StringValues(sessionId);
        if (correlationId is not null) ctx.Request.Headers["X-Correlation-Id"] = new StringValues(correlationId);
        return ctx;
    }

    [Fact]
    public async Task InvokeAsync_WhenJwtSubClaimPresent_SetsUserIdBaggage()
    {
        // ARRANGE — CorrelationIdMiddleware is the HTTP entry-point for context propagation.
        // It MUST run AFTER UseAuthentication so context.User is populated; that's enforced
        // by the middleware order in MapDefaultEndpoints (see CLAUDE.md "Observability").
        // We simulate that wiring here by attaching an authenticated user with the
        // ClaimTypes.NameIdentifier (the JWT "sub") claim.
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-999")]));
            var ctx = BuildContext(user: user);
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            // ACT
            await sut.InvokeAsync(ctx);

            // ASSERT — user.id baggage is set to the JWT sub. This baggage is what
            // OutgoingContextMiddleware reads when stamping the X-User-Id header onto
            // outgoing Wolverine messages — so every downstream log line in the saga
            // carries the originating user. If this regresses, the cross-service audit
            // trail breaks silently (logs still appear but with null UserId scope).
            activity.GetBaggageItem("user.id").Should().Be("user-999");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenNoUserClaim_DoesNotSetUserIdBaggage()
    {
        // ARRANGE — Unauthenticated request (anonymous endpoint, or auth failed). Baggage
        // must NOT contain user.id — null/empty values pollute the scope and lead to log
        // queries returning unexpected matches ("UserId IS NULL" still matches).
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext();
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            // ACT
            await sut.InvokeAsync(ctx);

            // ASSERT — Baggage absent. The CLAUDE.md guidance "never add null/empty keys
            // to scope dictionaries" applies here too.
            activity.GetBaggageItem("user.id").Should().BeNull();
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenSessionIdHeaderPresent_SetsSessionIdBaggage()
    {
        // ARRANGE — Session ID is client-generated (browser/app UUID), passed in the
        // X-Session-Id header. Unlike UserId (JWT-derived, trusted), SessionId is just
        // a correlator — useful for "all requests from this browser session" queries.
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext(sessionId: "sess-abc");
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            // ACT
            await sut.InvokeAsync(ctx);

            // ASSERT — session.id baggage matches the header.
            activity.GetBaggageItem("session.id").Should().Be("sess-abc");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenNoSessionIdHeader_DoesNotSetSessionIdBaggage()
    {
        // ARRANGE — No X-Session-Id header (e.g. an internal/admin caller). Baggage stays
        // empty for session.id — same null-key-avoidance principle as user.id.
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext();
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            // ACT
            await sut.InvokeAsync(ctx);

            // ASSERT
            activity.GetBaggageItem("session.id").Should().BeNull();
        }
    }
}
