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

    // AutoProvision creates topics/subscriptions on the Service Bus namespace at host
    // startup. That's a real network operation against the configured endpoint, and it
    // happens even when Wolverine's external transports are otherwise stubbed —
    // provisioning fires before DisableAllExternalWolverineTransports() from a test
    // factory's ConfigureTestServices takes effect. Integration tests use a fake ASB
    // connection string ("sb://fake.servicebus.windows.net/...") so AutoProvision hangs
    // trying to resolve/connect, eventually timing out at the CI runner's job limit.
    //
    // Gate on a config flag (not environment) so tests can disable provisioning while
    // leaving the rest of the Development-gated code (EF migration, OpenAPI, Scalar)
    // intact. Defaults to true so dev/prod behavior is unchanged.
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
