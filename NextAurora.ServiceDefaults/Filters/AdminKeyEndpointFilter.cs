using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace NextAurora.ServiceDefaults.Filters;

public class AdminKeyEndpointFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = configuration["AdminApiKey"];

        if (string.IsNullOrEmpty(configuredKey))
            return Results.Problem(
                title: "Admin access not configured",
                statusCode: StatusCodes.Status403Forbidden);

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Admin-Key", out var providedKey)
            || providedKey != configuredKey)
            return Results.Unauthorized();

        return await next(context);
    }
}
