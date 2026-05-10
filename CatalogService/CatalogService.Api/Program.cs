using System.Threading.RateLimiting;
using CatalogService.Api.Endpoints;
using CatalogService.Api.Services;
using CatalogService.Application.Commands;
using CatalogService.Infrastructure;
using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
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
builder.Services.AddCatalogInfrastructure(builder.Configuration);

// L2 distributed cache (Redis). HybridCache discovers IDistributedCache and uses it as the
// distributed tier; without this registration, HybridCache would only use its in-process L1.
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("cache");
});

// L1 in-process cache + stampede-protected GetOrCreateAsync. .NET 10's HybridCache abstracts
// the two-tier flow so we don't hand-roll it. Per-call options (TTL, tags) live in
// HybridProductCache — this registration uses framework defaults globally.
builder.Services.AddHybridCache();

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    await app.Services.MigrateDatabaseAsync<CatalogDbContext>();
}

app.UseRateLimiter();
app.MapCatalogEndpoints();
app.MapGrpcService<CatalogGrpcService>();
await app.RunAsync();
