using System.Threading.RateLimiting;
using FluentValidation;
using JasperFx.Resources;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using PaymentService.Endpoints;
using PaymentService.Infrastructure;
using PaymentService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Fixed-window limiter for the manual /payments/process endpoint: 10 requests / 10s.
// Counter is in-process (ASP.NET Core's built-in `AddFixedWindowLimiter`). Correct here
// today — PaymentService isn't deployed yet, and the deployment plan starts it as
// single-instance. **If/when this service scales to 2+ Machines** for resilience, the
// in-memory counter silently multiplies the effective limit by N (each Machine enforces
// its own; a client hitting any Machine gets a fresh 10-allowance). Fix at that point:
// swap to a Redis-backed limiter, with the increment + TTL pair wrapped in a Lua `EVAL`
// so it's atomic under concurrency. Tracked as a Phase 3 deliverable in
// docs/full-saga-deployment-plan.md. Rule: Security Requirements → Rate Limiting.
// See CLAUDE.md.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("payments", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromSeconds(10);
        limiter.QueueLimit = 0;
    });
});

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    var azureServiceBus = opts.UseAzureServiceBus(connectionString);

    // AutoProvision creates topics/subscriptions at host startup. Test harnesses use a
    // fake ASB connection string that would hang the connection attempt. Gate on a config
    // flag so tests can disable provisioning while leaving the rest of the
    // Development-gated code (EF migration, OpenAPI) intact. See OrderService/Program.cs
    // for the full rationale.
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        azureServiceBus.AutoProvision();
    }

    // Publish outgoing events to their topics
    opts.PublishMessage<PaymentCompletedEvent>().ToAzureServiceBusTopic("payment-events");
    opts.PublishMessage<PaymentFailedEvent>().ToAzureServiceBusTopic("payment-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("order-events/payment-orders-sub");

    // Transactional outbox: persist outgoing messages to SQL Server in the same
    // transaction as the entity write, then dispatch via background flush.
    var paymentsDb = builder.Configuration.GetConnectionString("payments-db")!;
    opts.PersistMessagesWithSqlServer(paymentsDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    // Single-project assembly — Wolverine auto-discovers handlers from the entry assembly,
    // so no explicit IncludeAssembly call is needed.
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
    opts.AddConcurrencyRetry();
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddResourceSetupOnStartup();

builder.Services.AddOpenApi();

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
    app.MapScalarApiReference();
    await app.Services.MigrateDatabaseAsync<PaymentDbContext>();
}

app.UseRateLimiter();
app.MapPaymentEndpoints();
await app.RunAsync();
