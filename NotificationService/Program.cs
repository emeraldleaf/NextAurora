using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NotificationService.Features;
using NotificationService.Infrastructure;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    // Config-selectable transport (Messaging:Transport): RabbitMQ default, Azure Service Bus opt-in.
    // NotificationService is listen-only (the saga sink). See OrderService/Program.cs + CLAUDE.md.
    var transport = builder.Configuration["Messaging:Transport"] ?? "rabbitmq";
    if (string.Equals(transport, "azureservicebus", StringComparison.OrdinalIgnoreCase))
    {
        var azureServiceBus = opts.UseAzureServiceBus(connectionString);
        if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
        {
            azureServiceBus.AutoProvision();
        }
        opts.ListenToAzureServiceBusSubscription("notify-orders-sub", c => c.TopicName = "order-events");
        opts.ListenToAzureServiceBusSubscription("notify-payments-sub", c => c.TopicName = "payment-events");
        opts.ListenToAzureServiceBusSubscription("notify-shipping-sub", c => c.TopicName = "shipping-events");
        opts.ListenToAzureServiceBusQueue("send-notification");
    }
    else
    {
        // Bind a per-source queue to each event exchange, plus the direct send-notification queue.
        var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString)).AutoProvision();
        rabbit.BindExchange("order-events", ExchangeType.Fanout).ToQueue("notify-orders");
        rabbit.BindExchange("payment-events", ExchangeType.Fanout).ToQueue("notify-payments");
        rabbit.BindExchange("shipping-events", ExchangeType.Fanout).ToQueue("notify-shipping");
        opts.ListenToRabbitQueue("notify-orders");
        opts.ListenToRabbitQueue("notify-payments");
        opts.ListenToRabbitQueue("notify-shipping");
        opts.ListenToRabbitQueue("send-notification");
    }

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
