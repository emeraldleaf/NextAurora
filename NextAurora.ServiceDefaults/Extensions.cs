using Asp.Versioning;
using JasperFx.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using NextAurora.ServiceDefaults.Messaging;
using NextAurora.ServiceDefaults.Metrics;
using NextAurora.ServiceDefaults.Middleware;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Wolverine;
using Wolverine.ErrorHandling;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddExceptionHandler<NextAurora.ServiceDefaults.GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        builder.Services.AddSingleton<NextAuroraMetrics>();

        builder.Services.AddServiceDiscovery();

        builder.AddDefaultAuthentication();

        builder.AddFrontendCors();

        builder.AddNextAuroraApiVersioning();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("NextAurora");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("Azure.Messaging.ServiceBus")
                    // "NextAurora.Messaging" is the ActivitySource used by all Service Bus
                    // processors. Registering it here causes consumer spans to appear in the
                    // Aspire dashboard and any connected distributed tracing backend.
                    .AddSource("NextAurora.Messaging")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase)
                    )
                    .AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static void AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // ORDER MATTERS. CorrelationIdMiddleware sits BETWEEN UseAuthentication and
        // UseAuthorization for two reasons:
        //
        //   1. It reads ClaimTypes.NameIdentifier from context.User to populate the
        //      UserId scope key. UseAuthentication populates context.User, so the
        //      middleware must run AFTER it. Otherwise UserId is silently always null.
        //
        //   2. Placing it BEFORE UseAuthorization (rather than after) means the
        //      UserId scope is active during the Authorization step itself. Any
        //      401/403 denials get logged with the authenticated user's ID —
        //      preserving the audit trail for "user X tried to access resource they
        //      shouldn't." Running after Authorization would drop that signal,
        //      losing visibility into unauthorized-attempt patterns.
        //
        // UseExceptionHandler stays first so it wraps every error below. UseCors runs
        // before UseAuthentication so CORS preflights (OPTIONS, which never carry a
        // bearer token) are answered without touching the auth pipeline. The policy is
        // only registered when Frontend:AllowedOrigins is configured — services with no
        // browser-facing surface skip the middleware entirely.
        app.UseExceptionHandler();
        if (!string.IsNullOrWhiteSpace(app.Configuration["Frontend:AllowedOrigins"]))
        {
            app.UseCors(FrontendCorsPolicyName);
        }
        app.UseAuthentication();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthorization();

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks(HealthEndpointPath);

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }

    private const string FrontendCorsPolicyName = "frontend";

    /// <summary>
    /// Explicit CORS for the browser SPA — registered ONLY when <c>Frontend:AllowedOrigins</c>
    /// is configured (semicolon-separated origin list, injected per service by AppHost).
    /// Per CLAUDE.md "Security Requirements → CORS": explicit policy, known origins only —
    /// never <c>AllowAnyOrigin</c>. <c>X-Correlation-Id</c> is exposed so the SPA's
    /// observability surface can read the correlation ID off every response; without
    /// <c>WithExposedHeaders</c>, browsers hide non-safelisted response headers from JS even
    /// when the request itself succeeds.
    /// </summary>
    private static void AddFrontendCors<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var configured = builder.Configuration["Frontend:AllowedOrigins"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var origins = configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("X-Correlation-Id")));
    }

    private static void AddDefaultAuthentication<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var authority = builder.Configuration["Authentication:Authority"]
            ?? builder.Configuration["Keycloak:Url"];

        if (string.IsNullOrEmpty(authority))
        {
            // No identity provider configured — register auth services with no-op defaults
            // so UseAuthentication/UseAuthorization don't throw, but no tokens are validated.
            builder.Services.AddAuthentication();
            builder.Services.AddAuthorization();
            return;
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = builder.Configuration["Authentication:Audience"] ?? "nextaurora-api";
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidateIssuer = true,
                    ValidateLifetime = true,
                    // Explicit — JWT Bearer's implicit default already validates the
                    // signature against JWKS-discovered keys, but making it explicit
                    // makes the security posture auditable + prevents a future config
                    // change from accidentally disabling signature validation.
                    ValidateIssuerSigningKey = true,
                    // Default ClockSkew is 5 minutes — revoked/expired tokens remain
                    // accepted for 5 extra minutes, which is material on a 15-minute
                    // access-token lifetime. 30 seconds covers reasonable inter-server
                    // clock drift without giving attackers a long replay window.
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = "realm_access.roles",
                };
            });

        builder.Services.AddAuthorization();
    }

    /// <summary>
    /// URL-segment API versioning. Default version is 1.0; clients must include the version in
    /// the route (`/api/v1/...`). The ApiExplorer integration makes versioned endpoints visible
    /// in OpenAPI docs with version-aware group names. Called automatically by
    /// <see cref="AddServiceDefaults{TBuilder}"/>.
    ///
    /// <para>
    /// <b>Why URL-segment over header-based versioning:</b> URL versioning is what most public
    /// REST APIs (Stripe, GitHub, Twitter, AWS) use. Pros: visible in logs/dashboards, cacheable
    /// (HTTP caches key on URL), debuggable from a browser, plays well with Swagger. The
    /// "header versioning is more RESTful" argument is academic — the practical wins of URL
    /// versioning dominate.
    /// </para>
    /// <para>
    /// <b>Why <c>AssumeDefaultVersionWhenUnspecified = false</c>:</b> the version segment is
    /// required. Hitting <c>/api/products</c> returns 400, not silent v1. This avoids the
    /// classic mistake of silently treating un-versioned calls as v1, which makes future v2
    /// migrations a behavior-change debugging nightmare.
    /// </para>
    /// </summary>
    private static void AddNextAuroraApiVersioning<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = false;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                // 'v'VVV → groups become "v1", "v2", "v1.1" etc. in OpenAPI.
                options.GroupNameFormat = "'v'VVV";
                // Replace `{version:apiVersion}` placeholder with the actual version in
                // generated docs/URLs (so Swagger UI shows "/api/v1/products" not the template).
                options.SubstituteApiVersionInUrl = true;
            });
    }

    /// <summary>
    /// Canonical helper for registering a versioned route group at
    /// <c>/api/v{version}/{template}</c>. Every endpoint group in every service goes through
    /// this — that way the policy (default version, tag application, route prefix) can't drift
    /// across services. Equivalent to:
    /// <code>
    /// app.NewVersionedApi(tag)
    ///    .MapGroup("/api/v{version:apiVersion}/" + template)
    ///    .HasApiVersion(new ApiVersion(1, 0))
    ///    .WithTags(tag);
    /// </code>
    /// <para>
    /// <b>How v2 will work:</b> register a side-by-side group with the same template but
    /// <c>HasApiVersion(new ApiVersion(2, 0))</c>. Existing v1 callers keep hitting the v1
    /// handler unchanged.
    /// </para>
    /// <para>
    /// <b>SOLID — DRY without ceremony:</b> this method exists because the alternative — every
    /// endpoint extension repeating four lines of boilerplate — is the kind of duplication that
    /// rots over time (somebody copies it, somebody else types it slightly differently, the
    /// versioning policy quietly diverges). One helper, one rule, one place to change.
    /// </para>
    /// </summary>
    public static RouteGroupBuilder MapV1ApiGroup(this WebApplication app, string tag, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var trimmed = template.TrimStart('/');
        return app.NewVersionedApi(tag)
            .MapGroup($"/api/v{{version:apiVersion}}/{trimmed}")
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags(tag);
    }

    /// <summary>
    /// Registers Wolverine context propagation middleware for both directions:
    /// <list type="bullet">
    ///   <item><c>ContextPropagationMiddleware</c> (incoming) — reads CorrelationId/UserId/SessionId
    ///         from the envelope headers and opens a logger scope so every log line emitted by
    ///         the handler carries those fields. Mirrors what <c>CorrelationIdMiddleware</c>
    ///         does for HTTP requests.</item>
    ///   <item><c>OutgoingContextMiddleware</c> (outgoing) — copies those same IDs from
    ///         Activity baggage onto outbound envelope headers so the next service in the saga
    ///         picks them up.</item>
    /// </list>
    /// Call inside <c>UseWolverine()</c> in every service. Without this, observability falls
    /// apart at the Service Bus boundary — you can't trace one transaction across services.
    /// </summary>
    public static WolverineOptions AddNextAuroraContextPropagation(this WolverineOptions opts)
    {
        opts.Policies.AddMiddleware<ContextPropagationMiddleware>();
        opts.Policies.AddMiddleware<OutgoingContextMiddleware>();
        return opts;
    }

    /// <summary>
    /// On <see cref="DbUpdateConcurrencyException"/>, retry the message handler up to three
    /// times with increasing backoff (50ms, 100ms, 250ms). After exhaustion the message goes
    /// to the dead-letter queue.
    ///
    /// <para>
    /// <b>Why retry rather than fail:</b> concurrency conflicts in the saga are *expected* —
    /// when <c>PaymentCompletedEvent</c> and <c>ShipmentDispatchedEvent</c> arrive at OrderService
    /// near-simultaneously, both handlers fetch the same order, both try to mutate, one wins.
    /// The loser's retry refetches the now-updated order; its status guard then either no-ops
    /// (if the operation became invalid) or applies cleanly. This is the right behavior — not
    /// an error — and shouldn't surface as a 5xx.
    /// </para>
    /// <para>
    /// <b>Pairs with the HTTP path:</b> <c>GlobalExceptionHandler</c> catches the same exception
    /// on the HTTP side and returns 409 Conflict with a refetch-and-try-again hint.
    /// </para>
    /// <para>
    /// Call inside <c>UseWolverine()</c> in every service that handles events on tracked
    /// aggregates with concurrency tokens (Order, Payment, Shipping).
    /// </para>
    /// </summary>
    public static WolverineOptions AddConcurrencyRetry(this WolverineOptions opts)
    {
        opts.OnException<DbUpdateConcurrencyException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
        return opts;
    }

    /// <summary>
    /// Apply pending EF Core migrations at startup. Resolves the DbContext from a fresh DI
    /// scope, calls <c>Database.MigrateAsync</c>, returns.
    ///
    /// <para>
    /// <b>Dev-only by convention:</b> this is called inside <c>if (app.Environment.IsDevelopment())</c>
    /// in every service's Program.cs. Why not production? With multiple replicas behind a load
    /// balancer, all replicas would race to apply migrations on startup — the first wins, the
    /// rest see history-table conflicts. In production, migrations should run as a separate
    /// pre-deploy step, then app pods start without migration at all.
    /// </para>
    /// </summary>
    public static async Task MigrateDatabaseAsync<TContext>(this IServiceProvider services, CancellationToken ct = default)
        where TContext : DbContext
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.MigrateAsync(ct);
    }
}
