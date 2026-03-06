using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace NextAurora.ServiceDefaults.Messaging;

/// <summary>
/// Wolverine incoming-message middleware that restores the three observability identifiers
/// (CorrelationId, UserId, SessionId) from envelope headers into the current Activity baggage
/// and opens a structured logger scope — mirroring what CorrelationIdMiddleware does for HTTP.
///
/// Registration: opts.Policies.AddMiddleware&lt;ContextPropagationMiddleware&gt;()
///
/// The Before() method runs before every message handler. The scope stored in _scope is
/// disposed by Finally(), which Wolverine calls after the handler completes (success or failure).
/// Each Wolverine message dispatch creates a new middleware instance (transient lifetime), so
/// the _scope field is safe from concurrent access.
/// </summary>
public sealed class ContextPropagationMiddleware(ILogger<ContextPropagationMiddleware> logger)
{
    private IDisposable? _scope;

    public void Before(Envelope envelope)
    {
        var correlationId = GetHeader(envelope, "X-Correlation-Id")
            ?? envelope.CorrelationId?.ToString()
            ?? Activity.Current?.TraceId.ToString();

        var userId = GetHeader(envelope, "X-User-Id");
        var sessionId = GetHeader(envelope, "X-Session-Id");

        // Restore into Activity baggage so handlers read the same keys as HTTP-originated requests.
        if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
        if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
        if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

        // MA0002: always pass StringComparer when constructing Dictionary<string, T>.
        var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId,
            ["MessageId"] = envelope.Id.ToString()
        };
        if (userId is not null) scopeState["UserId"] = userId;
        if (sessionId is not null) scopeState["SessionId"] = sessionId;

        _scope = logger.BeginScope(scopeState);
    }

    public void Finally() => _scope?.Dispose();

    private static string? GetHeader(Envelope envelope, string key)
        => envelope.Headers?.TryGetValue(key, out var val) == true ? val : null;
}

/// <summary>
/// Outgoing Wolverine middleware that stamps X-User-Id and X-Session-Id envelope headers
/// from the current Activity baggage before a message is sent. Wolverine handles CorrelationId
/// automatically via OpenTelemetry trace context propagation.
///
/// Registration: opts.Policies.AddMiddleware&lt;OutgoingContextMiddleware&gt;()
/// </summary>
public sealed class OutgoingContextMiddleware
{
    private OutgoingContextMiddleware() { }

    public static void Before(Envelope envelope)
    {
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        if (userId is not null) envelope.Headers["X-User-Id"] = userId;
        if (sessionId is not null) envelope.Headers["X-Session-Id"] = sessionId;
    }
}
