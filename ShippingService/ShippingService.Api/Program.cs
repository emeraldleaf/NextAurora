using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Api.Endpoints;
using ShippingService.Application.Commands;
using ShippingService.Infrastructure;
using Wolverine;
using Wolverine.AzureServiceBus;

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
    opts.ListenToAzureServiceBusSubscription("payment-events/shipping-sub");

    opts.Discovery.IncludeAssembly(typeof(CreateShipmentCommand).Assembly);
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddShippingInfrastructure(builder.Configuration);

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

app.MapShippingEndpoints();
app.MapAdminEventEndpoints();
app.Run();
