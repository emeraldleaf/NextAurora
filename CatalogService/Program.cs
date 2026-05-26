using System.Threading.RateLimiting;
using CatalogService.Endpoints;
using CatalogService.Features;
using CatalogService.Grpc;
using CatalogService.Infrastructure;
using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("search", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromSeconds(10);
        limiter.QueueLimit = 0;
    });
});

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(CreateProductCommand).Assembly);
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddValidatorsFromAssemblyContaining<CreateProductCommand>();

// DemoMode bridge: some PaaS providers (Fly.io) restrict secret names to [A-Z0-9_], rejecting
// the hyphen in ConnectionStrings__catalog-db. We expose the connection string under the
// platform-compatible name CATALOG_DB_CONNECTION_STRING and copy it into the slot the
// Infrastructure layer expects. Only fires when DemoMode is on; the Aspire local-dev path
// (which writes ConnectionStrings__catalog-db directly via WithReference) is unchanged.
if (builder.Configuration.GetValue<bool>("DemoMode"))
{
    var demoCatalogDbConnection = builder.Configuration["CATALOG_DB_CONNECTION_STRING"];
    if (!string.IsNullOrWhiteSpace(demoCatalogDbConnection))
    {
        builder.Configuration["ConnectionStrings:catalog-db"] = demoCatalogDbConnection;
    }
}

builder.Services.AddCatalogInfrastructure(builder.Configuration);

// L2 distributed cache (Redis). HybridCache discovers IDistributedCache and uses it as the
// distributed tier; without this registration, HybridCache would only use its in-process L1.
// Skipped when no "cache" connection string is configured (e.g. single-replica demo deploys
// without Redis) — HybridCache then runs L1-only, which is a lossier but functional fallback.
var redisConnectionString = builder.Configuration.GetConnectionString("cache");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
    });
}

// L1 in-process cache + stampede-protected GetOrCreateAsync. .NET 10's HybridCache abstracts
// the two-tier flow so we don't hand-roll it. Per-call options (TTL, tags) live in
// HybridProductCache — this registration uses framework defaults globally.
builder.Services.AddHybridCache();

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

// In DemoMode the app runs behind a PaaS proxy (Fly/App Runner) that terminates TLS and
// forwards plain HTTP. Without trusting X-Forwarded-Proto, ASP.NET Core sees Request.Scheme
// as "http", and the OpenAPI generator emits server URLs as "http://..." — which the browser
// blocks as mixed content when Scalar tries to issue try-it-out requests from the HTTPS page.
// KnownNetworks/KnownProxies are cleared because Fly's proxy isn't localhost, which is what
// the default would otherwise restrict to.
if (builder.Configuration.GetValue<bool>("DemoMode"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

// UseForwardedHeaders must run before any middleware that reads Request.Scheme/Host —
// including the OpenAPI route handlers — otherwise they see the unwrapped HTTP from the
// proxy instead of the original HTTPS the client used. See the matching Configure<> call
// in the service-registration section.
if (app.Configuration.GetValue<bool>("DemoMode"))
{
    app.UseForwardedHeaders();
}

app.MapDefaultEndpoints();

// DemoMode is a deliberate security relaxation for demo / portfolio deployments: it surfaces
// the OpenAPI spec + Scalar UI in non-Development environments. In real production these stay
// hidden because OpenAPI specs reveal the full attack surface (endpoints, schemas, auth shape).
// See docs/project-decisions.md "Why Scalar over Swagger UI" for the dev-only rationale.
var isDemoMode = app.Configuration.GetValue<bool>("DemoMode");
var showApiDocs = app.Environment.IsDevelopment() || isDemoMode;

// Skip HTTPS redirection in DemoMode — App Runner / most PaaS hosts terminate TLS at the edge
// and forward plain HTTP to the container; UseHttpsRedirection would cause a redirect loop.
if (!app.Environment.IsDevelopment() && !isDemoMode)
{
    app.UseHttpsRedirection();
}

if (showApiDocs)
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
}

// Migrate on startup in Development and DemoMode. Real production runs migrations as a
// separate deploy step (see CLAUDE.md "Migrations are immutable once applied").
if (app.Environment.IsDevelopment() || isDemoMode)
{
    await app.Services.MigrateDatabaseAsync<CatalogDbContext>();
}

app.UseRateLimiter();
app.MapCatalogEndpoints();
app.MapGrpcService<CatalogGrpcService>();
await app.RunAsync();

// Exposes the implicit top-level-statement Program class to the integration test project
// (tests/CatalogService.Tests.Integration) so it can drive WebApplicationFactory<Program>.
#pragma warning disable S1118 // Not a utility class — this is the ASP.NET Core WebApplicationFactory entry-point idiom.
public partial class Program;
#pragma warning restore S1118
