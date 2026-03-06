using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace PaymentService.Application.Behaviors;

/// <summary>
/// MediatR pipeline behaviour that runs around every command and query handler.
///
/// Pipeline order (outermost → innermost):
///   ValidationBehavior → LoggingBehavior → YourHandler
///
/// What it does:
///   1. Reads CorrelationId, UserId, and SessionId from the current Activity's baggage.
///      These were placed there by CorrelationIdMiddleware (HTTP requests) or the Service Bus
///      processor (async messages).  Activity.Current may be null in unit tests — all reads
///      use ?. so they return null silently rather than throwing.
///
///   2. Opens a logger.BeginScope() for the duration of the handler call.
///      This is the key step: every log line written anywhere inside the handler
///      — including deep inside repositories and domain services — will automatically
///      carry CorrelationId, UserId, and SessionId in the structured output.
///      The handler classes themselves don't need to pass these values around manually.
///
///   3. Logs "Handling {RequestName}" at the start and elapsed time at the end via a
///      Stopwatch. Using finally instead of catch-and-rethrow keeps the exception's original
///      stack trace intact and avoids the SonarAnalyzer S2139 warning.
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        // Read baggage items set by CorrelationIdMiddleware or the Service Bus processor.
        // Fall back to the raw trace ID if no "correlation.id" baggage was explicitly set
        // (covers cases where the middleware hasn't run, such as background jobs).
        var correlationId = Activity.Current?.GetBaggageItem("correlation.id") ?? Activity.Current?.TraceId.ToString();
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        // Build the scope dictionary.  StringComparer.Ordinal is required by the MA0002 analyzer
        // rule — always pass a comparer when constructing a Dictionary<string, T>.
        // Only add UserId/SessionId when they are present; an empty/null entry creates noise in logs.
        var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId
        };
        if (userId is not null) scopeState["UserId"] = userId;
        if (sessionId is not null) scopeState["SessionId"] = sessionId;

        // Open the scope.  Everything inside the using block — including the handler and any
        // class it calls — will emit log lines that include the scope state automatically.
        using (logger.BeginScope(scopeState))
        {
            logger.LogInformation("Handling {RequestName}", requestName);

            var sw = Stopwatch.StartNew();
            var succeeded = false;
            try
            {
                var response = await next();
                succeeded = true;
                return response;
            }
            finally
            {
                // finally always runs, whether the handler succeeded or threw.
                // Using a 'succeeded' flag avoids catch-log-rethrow which would replace the
                // original exception's stack trace and is flagged by SonarAnalyzer S2139.
                sw.Stop();
                if (succeeded)
                    logger.LogInformation("Handled {RequestName} in {ElapsedMs}ms", requestName, sw.ElapsedMilliseconds);
                else
                    logger.LogWarning("Failed {RequestName} after {ElapsedMs}ms", requestName, sw.ElapsedMilliseconds);
            }
        }
    }
}
