using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace NextAurora.ServiceDefaults.Filters;

/// <summary>
/// Minimal API endpoint filter that protects admin endpoints with a pre-shared API key.
///
/// Callers must include the header:
///   X-Admin-Key: &lt;value matching AdminApiKey in configuration&gt;
///
/// Design decisions:
///   - Fail-closed: if AdminApiKey is not configured at all, every request returns 403 (Forbidden).
///     This is intentional — a misconfigured deployment should deny access rather than open it.
///   - 401 vs 403: returns 401 (Unauthorised) when a key is provided but wrong. Returns 403
///     (Forbidden) when the server isn't configured to accept any key at all. The distinction
///     helps operators diagnose whether the problem is on the client side or the server side.
///
/// Usage — apply to a route group in Program.cs:
///   app.MapGroup("/admin").AddEndpointFilter&lt;AdminKeyEndpointFilter&gt;();
///
/// Configuration (appsettings.json / environment variable):
///   "AdminApiKey": "your-secret-key-here"
/// </summary>
public class AdminKeyEndpointFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = configuration["AdminApiKey"];

        // If the key is not in configuration, the endpoint is effectively disabled.
        // Returning 403 (not 401) signals that the server itself has not been set up for admin access,
        // rather than implying the client could succeed with correct credentials.
        if (string.IsNullOrEmpty(configuredKey))
            return Results.Problem(
                title: "Admin access not configured",
                statusCode: StatusCodes.Status403Forbidden);

        // Reject requests that either omit the header entirely or supply the wrong value.
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Admin-Key", out var providedKey)
            || providedKey != configuredKey)
            return Results.Unauthorized();

        // Key is correct — hand off to the next filter or the endpoint handler.
        return await next(context);
    }
}
