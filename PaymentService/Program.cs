using System.Threading.RateLimiting;
using FluentValidation;
using JasperFx.Resources;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using NextAurora.Contracts.Messaging;
using PaymentService.Endpoints;
using PaymentService.Infrastructure;
using PaymentService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;
using NextAurora.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Fixed-window limiter for the manual /payments/process endpoint: 10 requests / 10s.
// Counter is in-process (ASP.NET Core's built-in `AddFixedWindowLimiter`). Correct here
// today — PaymentService is deployed single-replica (docker compose on the shared box,
// one container per service). **If/when this service scales to 2+ Machines** for resilience, the
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

    // RabbitMQ transport. Wolverine declares the exchange/queue/binding via AutoProvision (gated so
    // it's off for transport-stubbed integration tests). See OrderService/Program.cs + CLAUDE.md.
    var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString));
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        rabbit.AutoProvision();
    }
    // Publisher-side topology: this service owns payment-events, so it declares the exchange AND
    // every consumer queue bound to it — a fanout exchange with zero bindings silently DISCARDS
    // publishes, and consumers only declare their own bindings at their own startup, so without
    // this a fresh broker has a first-boot loss window. Names come from MessagingExchanges/MessagingQueues (a
    // typo'd inline literal is silently auto-provisioned as a new empty object). See CLAUDE.md (#168).
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.OrderPayments);
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.ShippingPayments);
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyPayments);
    // Consumer-side binding (also declared by its publisher; declarations are idempotent).
    rabbit.BindExchange(MessagingExchanges.OrderEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.PaymentOrders);
    opts.PublishMessage<PaymentCompletedEvent>().ToRabbitExchange(MessagingExchanges.PaymentEvents);
    opts.PublishMessage<PaymentFailedEvent>().ToRabbitExchange(MessagingExchanges.PaymentEvents);
    opts.ListenToRabbitQueue(MessagingQueues.PaymentOrders);

    // Transactional outbox: persist outgoing messages to SQL Server in the same
    // transaction as the entity write, then dispatch via background flush.
    var paymentsDb = builder.Configuration.GetConnectionString("payments-db")!;
    opts.PersistMessagesWithSqlServer(paymentsDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    // Durable INBOX too — durability is per-direction: without this, listeners run buffered
    // (broker acked before handlers run; a crash loses the buffer). See CLAUDE.md (#169).
    opts.Policies.UseDurableInboxOnAllListeners();

    // Single-project assembly — Wolverine auto-discovers handlers from the entry assembly,
    // so no explicit IncludeAssembly call is needed.
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AllowHandlerServiceLocation();
    opts.AddNextAuroraContextPropagation();
    opts.AddConcurrencyRetry();
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddResourceSetupOnStartup();

builder.Services.AddOpenApi();

builder.Services.AddTurnstileVerification(builder.Configuration);

var app = builder.Build();

app.MapDefaultEndpoints();

var isDemoMode = app.Configuration.GetValue<bool>("DemoMode");

// Skip HTTPS redirection in DemoMode — TLS terminates at the edge proxy (Caddy), which
// forwards plain HTTP; redirection here would loop (and logs a failed-port warning).
if (!app.Environment.IsDevelopment() && !isDemoMode)
{
    app.UseHttpsRedirection();
}

// Migrate on startup in Development AND DemoMode (mirrors CatalogService). The deployed
// demo runs as Production behind Caddy with DemoMode=true; gating on Development alone
// left the EF tables missing on first deploy while Wolverine's own store migrated fine
// ("Invalid object name 'Orders'" on the first live POST /orders — 2026-08-27).
if (app.Environment.IsDevelopment() || isDemoMode)
{
    await app.Services.MigrateDatabaseAsync<PaymentDbContext>();
}

// API docs ship in DemoMode too (mirrors CatalogService) — the deployed demo's Scalar
// explorer at /scalar/v1 is part of the demo surface.
if (app.Environment.IsDevelopment() || isDemoMode)
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
}

app.UseRateLimiter();
app.MapPaymentEndpoints();
app.MapDemoEndpoints(); // kill switch — mapped only when DemoMode=true (see DemoEndpoints)
await app.RunAsync();
