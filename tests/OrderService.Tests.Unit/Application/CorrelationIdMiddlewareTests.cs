using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using NextAurora.ServiceDefaults.Middleware;

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
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-999")]));
            var ctx = BuildContext(user: user);
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            await sut.InvokeAsync(ctx);

            activity.GetBaggageItem("user.id").Should().Be("user-999");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenNoUserClaim_DoesNotSetUserIdBaggage()
    {
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext();
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            await sut.InvokeAsync(ctx);

            activity.GetBaggageItem("user.id").Should().BeNull();
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenSessionIdHeaderPresent_SetsSessionIdBaggage()
    {
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext(sessionId: "sess-abc");
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            await sut.InvokeAsync(ctx);

            activity.GetBaggageItem("session.id").Should().Be("sess-abc");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenNoSessionIdHeader_DoesNotSetSessionIdBaggage()
    {
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var ctx = BuildContext();
            var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask, _logger);

            await sut.InvokeAsync(ctx);

            activity.GetBaggageItem("session.id").Should().BeNull();
        }
    }
}
