using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NotificationService.Features;
using NotificationService.Infrastructure;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    // RabbitMQ transport. NotificationService is listen-only (the saga sink): bind a per-source
    // queue to each event exchange, plus the direct send-notification queue. AutoProvision is gated
    // (default on) for consistency with the other services. See OrderService/Program.cs + CLAUDE.md.
    var rabbit = opts.UseRabbitMq(factory => factory.Uri = new Uri(connectionString));
    if (builder.Configuration.GetValue("Wolverine:AutoProvision", defaultValue: true))
    {
        rabbit.AutoProvision();
    }
    rabbit.BindExchange("order-events", ExchangeType.Fanout).ToQueue("notify-orders");
    rabbit.BindExchange("payment-events", ExchangeType.Fanout).ToQueue("notify-payments");
    rabbit.BindExchange("shipping-events", ExchangeType.Fanout).ToQueue("notify-shipping");
    opts.ListenToRabbitQueue("notify-orders");
    opts.ListenToRabbitQueue("notify-payments");
    opts.ListenToRabbitQueue("notify-shipping");
    opts.ListenToRabbitQueue("send-notification");

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
