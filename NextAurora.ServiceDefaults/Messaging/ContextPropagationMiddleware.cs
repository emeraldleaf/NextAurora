using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace NextAurora.ServiceDefaults.Messaging;

/// <summary>
/// Wolverine incoming-message middleware: restores the three observability identifiers
/// (<c>CorrelationId</c>, <c>UserId</c>, <c>SessionId</c>) from envelope headers into the
/// current <c>Activity</c> baggage and opens a structured logger scope. Mirrors what
/// <c>CorrelationIdMiddleware</c> does for incoming HTTP requests, but for incoming Service Bus
/// messages — so the same scope keys appear on every log line whether the work was triggered
/// over HTTP or async messaging.
///
/// <para>
/// <b>SOLID — SRP and centralization:</b> handlers don't extract these IDs themselves. If they
/// did, every handler would repeat the same null-check ceremony, and we'd have ~15 places that
/// could drift. Putting it in middleware means the rule lives in one file and applies uniformly.
/// </para>
/// <para>
/// <b>Why a class field for <c>_scope</c> is safe:</b> Wolverine instantiates middleware fresh
/// per message dispatch (transient lifetime), so two concurrent messages get two instances —
/// no shared state, no race. <see cref="Before"/> opens the scope, <see cref="Finally"/>
/// disposes it after the handler completes (success or failure). Wolverine guarantees Finally
/// runs.
/// </para>
/// <para>
/// <b>Registration:</b> <c>opts.Policies.AddMiddleware&lt;ContextPropagationMiddleware&gt;()</c>
/// (handled inside <c>AddNextAuroraContextPropagation()</c> in <c>Extensions.cs</c>).
/// </para>
/// </summary>
public sealed class ContextPropagationMiddleware(ILogger<ContextPropagationMiddleware> logger)
{
    private IDisposable? _scope;

    public void Before(Envelope envelope)
    {
        // Three sources tried in order: explicit header (set by OutgoingContextMiddleware on
        // the sender), the envelope's built-in CorrelationId field (Wolverine sets this from
        // the W3C traceparent), and finally the current trace ID. We always end up with
        // *something* for correlation — never null.
        var correlationId = GetHeader(envelope, "X-Correlation-Id")
            ?? envelope.CorrelationId?.ToString()
            ?? Activity.Current?.TraceId.ToString();

        var userId = GetHeader(envelope, "X-User-Id");
        var sessionId = GetHeader(envelope, "X-Session-Id");

        // Activity baggage: travels with the current OpenTelemetry trace. Other code reading
        // these IDs (metrics tags, downstream calls) uses the dot-separated lowercase keys that
        // match the W3C baggage convention.
        if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
        if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
        if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

        // Logger scope: BeginScope opens an ambient context — every ILogger.Log call inside
        // the handler (including ones from repositories, domain services, anywhere transitively
        // called) will have these fields attached to the structured output.
        // MA0002: always pass StringComparer when constructing Dictionary<string, T>.
        // We don't include null values — empty fields in structured logs are noise.
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
/// Outgoing-message Wolverine middleware: stamps <c>X-User-Id</c> and <c>X-Session-Id</c>
/// envelope headers from the current <c>Activity</c> baggage before a message goes out.
/// CorrelationId is intentionally NOT stamped here — Wolverine stamps it automatically via
/// OpenTelemetry trace context propagation, so duplicating it would be wasted bytes.
///
/// <para>
/// <b>Why a static <c>Before</c> method:</b> there's no per-instance state and no DI dependencies
/// — the only inputs are the envelope and the ambient Activity baggage. Static avoids the
/// allocation of an instance per dispatch.
/// </para>
/// <para>
/// <b>Registration:</b> <c>opts.Policies.AddMiddleware&lt;OutgoingContextMiddleware&gt;()</c>
/// (handled inside <c>AddNextAuroraContextPropagation()</c>).
/// </para>
/// </summary>
public sealed class OutgoingContextMiddleware
{
    // Private constructor: this class is never instantiated. Keeps the static analyzer happy
    // about "class with only static members" without forcing the type itself to be static
    // (Wolverine's middleware discovery looks for instance method signatures; static *method*
    // is fine but the type can't be `static class`).
    private OutgoingContextMiddleware() { }

    public static void Before(Envelope envelope)
    {
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        // Only set non-null headers. An empty string in a header is more confusing than the
        // header simply being absent — debugging "Why is UserId blank?" leads down a rabbit hole.
        if (userId is not null) envelope.Headers["X-User-Id"] = userId;
        if (sessionId is not null) envelope.Headers["X-Session-Id"] = sessionId;
    }
}
