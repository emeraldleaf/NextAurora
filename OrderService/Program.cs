using FluentValidation;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Endpoints;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.RabbitMQ;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;

    // Channel names shared across both transport branches (ASB topic == RabbitMQ exchange).
    const string orderEvents = "order-events";
    const string paymentEvents = "payment-events";
    const string shippingEvents = "shipping-events";

    // Messaging transport is config-selectable via Messaging:Transport: RabbitMQ (default — local
    // dev, CI, and the Hetzner deployment) or Azure Service Bus (Azure deployments). Wolverine
    // abstracts the broker, so ONLY this block differs between transports — handlers, the outbox,
    // idempotency, and the saga are identical. RabbitMQ maps ASB topics → fanout exchanges and ASB
    // subscriptions → queues bound to them. See CLAUDE.md + docs/full-saga-deployment-plan.md (D3).
    var transport = builder.Configuration["Messaging:Transport"] ?? "rabbitmq";
    if (string.Equals(transport, "azureservicebus", StringComparison.OrdinalIgnoreCase))
    {
        var azureServiceBus = opts.UseAzureServiceBus(connectionString);
        // AutoProvision uses the ASB management API. Gate it (default true) so it's OFF for
        // integration tests (fake connection string would hang) but ON for real Azure. See CLAUDE.md.
        if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
        {
            azureServiceBus.AutoProvision();
        }
        opts.PublishMessage<OrderPlacedEvent>().ToAzureServiceBusTopic(orderEvents);
        opts.ListenToAzureServiceBusSubscription("order-payments-sub", c => c.TopicName = paymentEvents);
        opts.ListenToAzureServiceBusSubscription("order-shipping-sub", c => c.TopicName = shippingEvents);
    }
    else
    {
        // RabbitMQ auto-provisions against a real broker (no emulator limitations). Declare the
        // exchange we publish to as fanout, and bind each queue we consume to its source exchange.
        var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString)).AutoProvision();
        rabbit.DeclareExchange(orderEvents, e => e.ExchangeType = ExchangeType.Fanout);
        rabbit.BindExchange(paymentEvents, ExchangeType.Fanout).ToQueue("order-payments");
        rabbit.BindExchange(shippingEvents, ExchangeType.Fanout).ToQueue("order-shipping");
        opts.PublishMessage<OrderPlacedEvent>().ToRabbitExchange(orderEvents);
        opts.ListenToRabbitQueue("order-payments");
        opts.ListenToRabbitQueue("order-shipping");
    }

    // Transactional outbox: persist outgoing messages to SQL Server in the same
    // transaction as the entity write, then dispatch via background flush.
    var ordersDb = builder.Configuration.GetConnectionString("orders-db")!;
    opts.PersistMessagesWithSqlServer(ordersDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOpenApi("/openapi/{documentName}.yaml");
    app.MapScalarApiReference();
    await app.Services.MigrateDatabaseAsync<OrderDbContext>();
}

app.MapOrderEndpoints();
await app.RunAsync();

// Exposes the implicit top-level-statement Program class to the integration test project
// (tests/OrderService.Tests.Integration) so it can drive WebApplicationFactory<Program>.
#pragma warning disable S1118 // Not a utility class — this is the ASP.NET Core WebApplicationFactory entry-point idiom.
public partial class Program;
#pragma warning restore S1118
