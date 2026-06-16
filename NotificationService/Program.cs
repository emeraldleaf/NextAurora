using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NotificationService.Features;
using NotificationService.Infrastructure;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;

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

    // Listen to events from other services
    opts.ListenToAzureServiceBusSubscription("notify-orders-sub", c => c.TopicName = "order-events");
    opts.ListenToAzureServiceBusSubscription("notify-payments-sub", c => c.TopicName = "payment-events");
    opts.ListenToAzureServiceBusSubscription("notify-shipping-sub", c => c.TopicName = "shipping-events");

    // Listen to direct command queue
    opts.ListenToAzureServiceBusQueue("send-notification");

    // Single-project assembly — Wolverine auto-discovers handlers from the entry assembly,
    // so no explicit IncludeAssembly call is needed.
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AllowHandlerServiceLocation();
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddNotificationInfrastructure(builder.Configuration);

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
}

app.Run();
