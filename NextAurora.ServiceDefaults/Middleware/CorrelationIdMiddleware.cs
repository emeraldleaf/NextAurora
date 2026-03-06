using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NextAurora.ServiceDefaults.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private const string SessionIdHeader = "X-Session-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var sessionId = context.Request.Headers[SessionIdHeader].FirstOrDefault();

        // Propagate through distributed tracing baggage so publishers and behaviors can read them
        Activity.Current?.SetBaggage("correlation.id", correlationId);
        if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
        if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

        // Echo back to caller
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId
        };
        if (userId is not null) scopeState["UserId"] = userId;
        if (sessionId is not null) scopeState["SessionId"] = sessionId;

        using (logger.BeginScope(scopeState))
        {
            await next(context);
        }
    }
}
