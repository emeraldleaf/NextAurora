using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Endpoints;
using ShippingService.Infrastructure;
using ShippingService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.RabbitMQ;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;

    // Channel names shared across both transport branches (ASB topic == RabbitMQ exchange).
    const string shippingEvents = "shipping-events";
    const string paymentEvents = "payment-events";

    // Config-selectable transport (Messaging:Transport): RabbitMQ default, Azure Service Bus opt-in.
    // Only this block differs between transports. See OrderService/Program.cs + CLAUDE.md.
    var transport = builder.Configuration["Messaging:Transport"] ?? "rabbitmq";
    if (string.Equals(transport, "azureservicebus", StringComparison.OrdinalIgnoreCase))
    {
        var azureServiceBus = opts.UseAzureServiceBus(connectionString);
        if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
        {
            azureServiceBus.AutoProvision();
        }
        opts.PublishMessage<ShipmentDispatchedEvent>().ToAzureServiceBusTopic(shippingEvents);
        opts.ListenToAzureServiceBusSubscription("shipping-payments-sub", c => c.TopicName = paymentEvents);
    }
    else
    {
        var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString)).AutoProvision();
        rabbit.DeclareExchange(shippingEvents, e => e.ExchangeType = ExchangeType.Fanout);
        rabbit.BindExchange(paymentEvents, ExchangeType.Fanout).ToQueue("shipping-payments");
        opts.PublishMessage<ShipmentDispatchedEvent>().ToRabbitExchange(shippingEvents);
        opts.ListenToRabbitQueue("shipping-payments");
    }

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
