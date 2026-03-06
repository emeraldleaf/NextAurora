using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NextAurora.ServiceDefaults.Middleware;

/// <summary>
/// ASP.NET Core middleware that runs on every inbound HTTP request and establishes the three
/// observability identifiers used throughout the platform:
///
///   CorrelationId — uniquely identifies a full transaction (e.g. one order placement).
///                   If the caller provides X-Correlation-Id we reuse it; otherwise we generate
///                   one from the current OpenTelemetry trace ID, or a new GUID as a last resort.
///
///   UserId        — the authenticated user (extracted from the JWT "sub" claim). Null for
///                   unauthenticated / anonymous requests; code downstream must handle that.
///
///   SessionId     — an opaque ID the frontend generates once per browser/app session and sends
///                   in X-Session-Id. Lets you correlate all actions from one browsing session.
///
/// All three values are stored in two places:
///   1. Activity.Current baggage  — accessed by ServiceBusEventPublisher when building outgoing
///      messages, and by LoggingBehavior when enriching MediatR handler logs.
///   2. logger.BeginScope()       — automatically prepended to every log line written during
///      this request, including deep inside handlers and repositories.
///
/// This middleware is registered once in ServiceDefaults and shared by all API services.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private const string SessionIdHeader = "X-Session-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        // Prefer the caller-supplied ID so distributed traces can be joined across systems.
        // Fall back to the OpenTelemetry trace ID (present when Aspire/OTel is configured),
        // then to a freshly generated GUID.
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        // ClaimTypes.NameIdentifier maps to the JWT "sub" claim — the canonical user identifier.
        // Returns null when the request is unauthenticated; downstream code must not assume it is present.
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // X-Session-Id is client-generated (a UUID the browser/app creates once and reuses).
        // It is not authenticated, but is useful for grouping log lines within a single session.
        var sessionId = context.Request.Headers[SessionIdHeader].FirstOrDefault();

        // Activity baggage travels with the current distributed trace.
        // ServiceBusEventPublisher reads these keys when building outgoing messages so the IDs
        // survive the hop across the message bus to the next service.
        // Dot-separated lowercase keys follow the W3C baggage / OpenTelemetry convention.
        Activity.Current?.SetBaggage("correlation.id", correlationId);
        if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
        if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

        // Echo the correlation ID back to the caller so they can quote it in support requests.
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // BeginScope attaches key-value pairs to every log line written while the scope is open.
        // Because the scope wraps the entire downstream pipeline (via await next(context)),
        // ALL log lines for this request — across handlers, repositories, domain services —
        // automatically carry CorrelationId, UserId, and SessionId without those classes needing
        // to know about them.  Only add a key when the value is non-null to avoid noisy empty fields.
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
