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
    opts.UseAzureServiceBus(connectionString)
        .AutoProvision();

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
