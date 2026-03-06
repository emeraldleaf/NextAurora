using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CatalogService.Application.Behaviors;

public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var correlationId = Activity.Current?.GetBaggageItem("correlation.id") ?? Activity.Current?.TraceId.ToString();
        var userId = Activity.Current?.GetBaggageItem("user.id");
        var sessionId = Activity.Current?.GetBaggageItem("session.id");

        var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId
        };
        if (userId is not null) scopeState["UserId"] = userId;
        if (sessionId is not null) scopeState["SessionId"] = sessionId;

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
                sw.Stop();
                if (succeeded)
                    logger.LogInformation("Handled {RequestName} in {ElapsedMs}ms", requestName, sw.ElapsedMilliseconds);
                else
                    logger.LogWarning("Failed {RequestName} after {ElapsedMs}ms", requestName, sw.ElapsedMilliseconds);
            }
        }
    }
}
