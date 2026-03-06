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

        logger.LogInformation("Handling {RequestName} (CorrelationId: {CorrelationId})", requestName, correlationId);

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
                logger.LogInformation("Handled {RequestName} in {ElapsedMs}ms (CorrelationId: {CorrelationId})", requestName, sw.ElapsedMilliseconds, correlationId);
            else
                logger.LogWarning("Failed {RequestName} after {ElapsedMs}ms (CorrelationId: {CorrelationId})", requestName, sw.ElapsedMilliseconds, correlationId);
        }
    }
}
