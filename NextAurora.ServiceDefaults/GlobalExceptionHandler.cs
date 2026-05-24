using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NextAurora.ServiceDefaults;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const string TraceIdKey = "traceId";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Use TraceId, NOT Activity.Current?.Id. Activity.Id is the full W3C
        // traceparent ("00-<trace>-<span>-<flags>") — returning that to clients
        // leaks the span ID, which is information about server-side handler call
        // structure that clients have no business seeing. Trace ID alone is the
        // correlation token clients legitimately need. See CLAUDE.md "Security
        // Requirements — Error Handling: Never expose internal state".
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);

        var problemDetails = exception switch
        {
            FluentValidation.ValidationException validationException => CreateValidationProblemDetails(validationException, traceId),
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrent modification",
                Detail = "The resource was modified by another request. Refetch and try again.",
                Extensions = { [TraceIdKey] = traceId }
            },
            UnauthorizedAccessException => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "You are not permitted to perform this operation on this resource.",
                Extensions = { [TraceIdKey] = traceId }
            },
            ArgumentException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = "One or more request parameters are invalid.",
                Extensions = { [TraceIdKey] = traceId }
            },
            InvalidOperationException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Operation not allowed",
                Detail = "The requested operation is not valid for the current state.",
                Extensions = { [TraceIdKey] = traceId }
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred",
                Detail = "Please contact support with the trace ID.",
                Extensions = { [TraceIdKey] = traceId }
            }
        };

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }

    private static ProblemDetails CreateValidationProblemDetails(FluentValidation.ValidationException exception, string? traceId)
    {
        var errors = exception.Errors
            .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);

        return new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Detail = "One or more validation errors occurred.",
            Extensions =
            {
                [TraceIdKey] = traceId,
                ["errors"] = errors
            }
        };
    }
}
