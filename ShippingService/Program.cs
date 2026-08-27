using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using NextAurora.Contracts.Messaging;
using ShippingService.Endpoints;
using ShippingService.Infrastructure;
using ShippingService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

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
    // Publisher-side topology: this service owns shipping-events, so it declares the exchange AND
    // every consumer queue bound to it — a fanout exchange with zero bindings silently DISCARDS
    // publishes, and consumers only declare their own bindings at their own startup, so without
    // this a fresh broker has a first-boot loss window. Names come from MessagingExchanges/MessagingQueues (a
    // typo'd inline literal is silently auto-provisioned as a new empty object). See CLAUDE.md (#168).
    rabbit.BindExchange(MessagingExchanges.ShippingEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.OrderShipping);
    rabbit.BindExchange(MessagingExchanges.ShippingEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyShipping);
    // Consumer-side binding (also declared by its publisher; declarations are idempotent).
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.ShippingPayments);
    opts.PublishMessage<ShipmentDispatchedEvent>().ToRabbitExchange(MessagingExchanges.ShippingEvents);
    opts.ListenToRabbitQueue(MessagingQueues.ShippingPayments);

    // Transactional outbox: persist outgoing messages to Postgres in the same
    // transaction as the entity write, then dispatch via background flush.
    var shippingDb = builder.Configuration.GetConnectionString("shipping-db")!;
    opts.PersistMessagesWithPostgresql(shippingDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    // Durable INBOX too — durability is per-direction: without this, listeners run buffered
    // (broker acked before handlers run; a crash loses the buffer). See CLAUDE.md (#169).
    opts.Policies.UseDurableInboxOnAllListeners();

    // Single-project assembly — Wolverine auto-discovers handlers from the entry assembly,
    // so no explicit IncludeAssembly call is needed.
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AllowHandlerServiceLocation();
    opts.AddNextAuroraContextPropagation();
    opts.AddConcurrencyRetry();
});
builder.Services.AddShippingInfrastructure(builder.Configuration);
builder.Services.AddResourceSetupOnStartup();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Migrate on startup in Development AND DemoMode (mirrors CatalogService). The deployed
// demo runs as Production behind Caddy with DemoMode=true; gating on Development alone
// left the EF tables missing on first deploy while Wolverine's own store migrated fine
// ("Invalid object name 'Orders'" on the first live POST /orders — 2026-08-27).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("DemoMode"))
{
    await app.Services.MigrateDatabaseAsync<ShippingDbContext>();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
}

app.MapShippingEndpoints();
await app.RunAsync();
