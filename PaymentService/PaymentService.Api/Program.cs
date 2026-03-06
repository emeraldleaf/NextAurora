using FluentValidation;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using PaymentService.Api.Endpoints;
using PaymentService.Application.Commands;
using PaymentService.Infrastructure;
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
    opts.PublishMessage<PaymentCompletedEvent>().ToAzureServiceBusTopic("payment-events");
    opts.PublishMessage<PaymentFailedEvent>().ToAzureServiceBusTopic("payment-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("order-events/payment-sub");

    opts.Discovery.IncludeAssembly(typeof(ProcessPaymentCommand).Assembly);
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
});
builder.Services.AddValidatorsFromAssemblyContaining<ProcessPaymentCommand>();
builder.Services.AddPaymentInfrastructure(builder.Configuration);

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

app.MapPaymentEndpoints();
app.MapAdminEventEndpoints();
app.Run();
