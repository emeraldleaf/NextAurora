using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NextAurora.Contracts.Messaging;
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
    rabbit.BindExchange(MessagingExchanges.OrderEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyOrders);
    rabbit.BindExchange(MessagingExchanges.PaymentEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyPayments);
    rabbit.BindExchange(MessagingExchanges.ShippingEvents, ExchangeType.Fanout)
        .ToQueue(MessagingQueues.NotifyShipping);
    // ProcessInline: durability is per-direction, and this service has NO message store (stateless,
    // no DB) — so a durable inbox isn't available. Inline processing acks the broker only AFTER the
    // handler completes, which restores consume-side at-least-once here: a crash mid-handle means
    // the broker redelivers (handlers are idempotent). See CLAUDE.md (#169).
    opts.ListenToRabbitQueue(MessagingQueues.NotifyOrders).ProcessInline();
    opts.ListenToRabbitQueue(MessagingQueues.NotifyPayments).ProcessInline();
    opts.ListenToRabbitQueue(MessagingQueues.NotifyShipping).ProcessInline();
    opts.ListenToRabbitQueue(MessagingQueues.SendNotification).ProcessInline();

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
