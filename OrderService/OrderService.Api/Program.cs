using CatalogService.Api.Grpc;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Api.Endpoints;
using OrderService.Api.GrpcClients;
using OrderService.Application.Commands;
using OrderService.Application.Interfaces;
using OrderService.Infrastructure;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.FluentValidation;

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
    opts.ListenToAzureServiceBusSubscription("payment-events/order-sub");
    opts.ListenToAzureServiceBusSubscription("shipping-events/order-sub");

    opts.Discovery.IncludeAssembly(typeof(PlaceOrderCommand).Assembly);
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddValidatorsFromAssemblyContaining<PlaceOrderCommand>();
builder.Services.AddOrderInfrastructure(builder.Configuration);

builder.Services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
{
    o.Address = new Uri("https+http://catalog-service");
});
builder.Services.AddScoped<ICatalogClient, GrpcCatalogClient>();

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

app.MapOrderEndpoints();
app.MapAdminEventEndpoints();
app.Run();
