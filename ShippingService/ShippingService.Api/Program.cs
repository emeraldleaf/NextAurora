using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Api.Endpoints;
using ShippingService.Application.Commands;
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
    opts.UseAzureServiceBus(connectionString)
        .AutoProvision();

    // Publish outgoing events to their topics
    opts.PublishMessage<ShipmentDispatchedEvent>().ToAzureServiceBusTopic("shipping-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("payment-events/shipping-payments-sub");

    // Transactional outbox: persist outgoing messages to Postgres in the same
    // transaction as the entity write, then dispatch via background flush.
    var shippingDb = builder.Configuration.GetConnectionString("shipping-db")!;
    opts.PersistMessagesWithPostgresql(shippingDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.Discovery.IncludeAssembly(typeof(CreateShipmentCommand).Assembly);
    opts.Policies.LogMessageStarting(LogLevel.Information);
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
