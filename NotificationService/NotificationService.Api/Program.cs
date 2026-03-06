using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Infrastructure;
using Wolverine;
using Wolverine.AzureServiceBus;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    opts.UseAzureServiceBus(connectionString)
        .AutoProvision();

    // Listen to events from other services
    opts.ListenToAzureServiceBusSubscription("order-events/notify-sub");
    opts.ListenToAzureServiceBusSubscription("payment-events/notify-sub");
    opts.ListenToAzureServiceBusSubscription("shipping-events/notify-sub");

    // Listen to direct command queue
    opts.ListenToAzureServiceBusQueue("send-notification");

    opts.Discovery.IncludeAssembly(typeof(SendNotificationRequest).Assembly);
    opts.Policies.LogMessageStarting(LogLevel.Information);
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
}

app.Run();
