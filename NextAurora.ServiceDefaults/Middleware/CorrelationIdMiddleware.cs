using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NextAurora.ServiceDefaults.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationIdHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        // Propagate through distributed tracing baggage
        Activity.Current?.SetBaggage("correlation.id", correlationId);

        // Echo back to caller
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
