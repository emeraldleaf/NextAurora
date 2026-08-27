using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NextAurora.ServiceDefaults;

/// <summary>
/// Cloudflare Turnstile verification for bot-sensitive endpoints (deployed demo: the demo
/// credentials are public, so JWT auth is not a gate against scripted abuse — Turnstile adds
/// a browser-interaction cost on the two actions worth abusing: placing an order (drives the
/// whole saga) and the kill switch (state-changing on a shared demo).
///
/// <para><b>Fail-closed by construction</b> (CLAUDE.md "Security Requirements"): the filter is
/// a no-op only when <c>Turnstile:Enabled=false</c> (explicit config, the local-dev default).
/// When enabled: a missing secret fails at startup, not silently open; a missing or invalid
/// token is a 403. There is no "skip if unconfigured" path.</para>
///
/// <para><b>Wiring:</b> <c>builder.Services.AddTurnstileVerification(builder.Configuration)</c>
/// in Program.cs, then <c>.RequireTurnstile()</c> on the endpoint. The SPA sends the token in
/// the <c>X-Turnstile-Token</c> header; tokens are single-use (Cloudflare rejects replays).</para>
/// </summary>
public static class TurnstileExtensions
{
    public const string TokenHeader = "X-Turnstile-Token";

    public static IServiceCollection AddTurnstileVerification(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Turnstile:Enabled");
        if (enabled && string.IsNullOrWhiteSpace(configuration["Turnstile:SecretKey"]))
        {
            // Enabled-without-secret must fail LOUDLY at startup: every verify would 403 and
            // the demo's checkout would be silently dead. Same fail-closed posture as
            // RequireHttpsMetadata (see AddDefaultAuthentication).
            throw new InvalidOperationException(
                "Turnstile:Enabled is true but Turnstile:SecretKey is not configured.");
        }

        services.AddHttpClient<ITurnstileVerifier, CloudflareTurnstileVerifier>(client =>
        {
            client.BaseAddress = new Uri("https://challenges.cloudflare.com");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }

    /// <summary>Require a valid Turnstile token on this endpoint (no-op when disabled).</summary>
    public static TBuilder RequireTurnstile<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder => endpointBuilder.FilterFactories.Add((_, next) =>
            async context =>
            {
                var http = context.HttpContext;
                var config = http.RequestServices.GetRequiredService<IConfiguration>();
                if (!config.GetValue<bool>("Turnstile:Enabled"))
                {
                    return await next(context);
                }

                var token = http.Request.Headers[TokenHeader].ToString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Verification token required.");
                }

                var verifier = http.RequestServices.GetRequiredService<ITurnstileVerifier>();
                var ok = await verifier.VerifyAsync(token, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
                return ok ? await next(context) : Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Verification failed.");
            }));
        return builder;
    }
}

/// <summary>Port for the siteverify call — substituted in integration tests (NSubstitute).</summary>
public interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken ct);
}

public sealed class CloudflareTurnstileVerifier : ITurnstileVerifier
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CloudflareTurnstileVerifier> _logger;

    public CloudflareTurnstileVerifier(HttpClient httpClient, IConfiguration configuration, ILogger<CloudflareTurnstileVerifier> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["secret"] = _configuration["Turnstile:SecretKey"] ?? string.Empty,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(new Uri("/turnstile/v0/siteverify", UriKind.Relative), content, ct);
            var result = await response.Content.ReadFromJsonAsync<SiteverifyResponse>(ct);
            if (result is not { Success: true })
            {
                _logger.LogWarning("Turnstile verification rejected: {ErrorCodes}", string.Join(",", result?.ErrorCodes ?? []));
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Cloudflare unreachable → fail CLOSED. A bot-gate that opens on outage isn't one.
            _logger.LogError(ex, "Turnstile siteverify call failed");
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Instantiated by System.Text.Json deserialization in VerifyAsync.")]
    private sealed record SiteverifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
