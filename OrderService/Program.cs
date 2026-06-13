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
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    var azureServiceBus = opts.UseAzureServiceBus(connectionString);

    // AutoProvision creates topics/subscriptions via the Service Bus *management* API
    // (ServiceBusAdministrationClient) at host startup — a real network operation against the
    // configured endpoint. Two environments must disable it:
    //   1. Integration tests use a fake ASB connection string, so provisioning hangs/times out
    //      (it fires before DisableAllExternalWolverineTransports() takes effect).
    //   2. Local dev (Aspire) uses the Service Bus emulator, which implements only the AMQP data
    //      plane — NOT the management API — so AutoProvision retries 4× and the host dies with
    //      BrokerInitializationException. The AppHost declares the topology and injects
    //      Wolverine__AutoProvision=false for each Wolverine service.
    // Gate on a config flag (defaults true) so real Azure / Publish mode still provisions. See CLAUDE.md.
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        azureServiceBus.AutoProvision();
    }

    // Publish outgoing events to their topics
    opts.PublishMessage<OrderPlacedEvent>().ToAzureServiceBusTopic("order-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("payment-events/order-payments-sub");
    opts.ListenToAzureServiceBusSubscription("shipping-events/order-shipping-sub");

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
