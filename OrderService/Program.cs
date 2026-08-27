using FluentValidation;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using NextAurora.Contracts.Messaging;
using OrderService.Endpoints;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;

    // RabbitMQ transport (local dev, CI, and the Hetzner deployment). Wolverine declares the
    // exchanges/queues/bindings via AutoProvision against the live broker. AutoProvision is gated
    // (default on) so it's OFF for integration tests, which stub the transport and would otherwise
    // block on the fake connection string. See CLAUDE.md.
    var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString));
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        rabbit.AutoProvision();
    }

    // Publisher-side topology: this service owns order-events, so it declares the exchange AND
    // every consumer queue bound to it. A fanout exchange with zero bindings silently DISCARDS
    // publishes, and consumers only declare their own bindings at their own startup — so without
    // this, a fresh broker has a first-boot window where OrderPlacedEvent is dropped while the
    // outbox marks it delivered (order wedged in Placed forever). Declaring consumers' queues here
    // guarantees the full order-events topology exists before this service's first publish.
    // Names come from MessagingExchanges/MessagingQueues — a typo'd inline literal would be silently
    // auto-provisioned as a new empty exchange/queue. See CLAUDE.md (#168).
    rabbit.BindExchange(MessagingExchanges.OrderEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.PaymentOrders);
    rabbit.BindExchange(MessagingExchanges.OrderEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyOrders);

    // Consumer-side bindings for the exchanges this service listens to (also declared by their
    // publishers — declarations are idempotent, and keeping both sides means neither boot order
    // leaves a gap).
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.OrderPayments);
    rabbit.BindExchange(MessagingExchanges.ShippingEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.OrderShipping);
    opts.PublishMessage<OrderPlacedEvent>().ToRabbitExchange(MessagingExchanges.OrderEvents);
    opts.ListenToRabbitQueue(MessagingQueues.OrderPayments);
    opts.ListenToRabbitQueue(MessagingQueues.OrderShipping);

    // Transactional outbox: persist outgoing messages to SQL Server in the same
    // transaction as the entity write, then dispatch via background flush.
    var ordersDb = builder.Configuration.GetConnectionString("orders-db")!;
    opts.PersistMessagesWithSqlServer(ordersDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    // Durable INBOX too — durability is per-direction. Without this, listeners run in Wolverine's
    // default buffered mode: the broker is acked as messages enter the in-memory buffer, BEFORE
    // handlers run, so a crash loses everything buffered (no redelivery — the ack already
    // happened). Durable inbox persists incoming envelopes to the message store first, making
    // consume-side at-least-once real. See CLAUDE.md (#169).
    opts.Policies.UseDurableInboxOnAllListeners();

    // Single-project assembly — Wolverine auto-discovers handlers from the entry assembly.
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AllowHandlerServiceLocation();
    opts.AddNextAuroraContextPropagation();
    opts.AddConcurrencyRetry();
});
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddOrderInfrastructure(builder.Configuration);
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
    await app.Services.MigrateDatabaseAsync<OrderDbContext>();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
}

app.MapOrderEndpoints();
await app.RunAsync();

// Exposes the implicit top-level-statement Program class to the integration test project
// (tests/OrderService.Tests.Integration) so it can drive WebApplicationFactory<Program>.
#pragma warning disable S1118 // Not a utility class — this is the ASP.NET Core WebApplicationFactory entry-point idiom.
public partial class Program;
#pragma warning restore S1118
