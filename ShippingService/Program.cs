using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Endpoints;
using ShippingService.Infrastructure;
using ShippingService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    var azureServiceBus = opts.UseAzureServiceBus(connectionString);

    // AutoProvision creates topics/subscriptions via the Service Bus management API at host
    // startup. Disabled in two environments: integration tests (fake ASB string hangs) and
    // local dev (the emulator's SUBSCRIPTION admin endpoints return HTTP 500 → AutoProvision
    // dies with BrokerInitializationException; the listener binds over AMQP without it). The
    // AppHost injects Wolverine__AutoProvision=false for the emulator. Gate on a config flag
    // (defaults true) so real Azure still provisions. See OrderService/Program.cs + CLAUDE.md.
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        azureServiceBus.AutoProvision();
    }

    // Publish outgoing events to their topics
    opts.PublishMessage<ShipmentDispatchedEvent>().ToAzureServiceBusTopic("shipping-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("shipping-payments-sub", c => c.TopicName = "payment-events");

    // Transactional outbox: persist outgoing messages to Postgres in the same
    // transaction as the entity write, then dispatch via background flush.
    var shippingDb = builder.Configuration.GetConnectionString("shipping-db")!;
    opts.PersistMessagesWithPostgresql(shippingDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
    await app.Services.MigrateDatabaseAsync<ShippingDbContext>();
}

app.MapShippingEndpoints();
await app.RunAsync();
