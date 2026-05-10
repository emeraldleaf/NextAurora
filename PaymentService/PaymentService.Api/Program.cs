using System.Threading.RateLimiting;
using FluentValidation;
using JasperFx.Resources;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using PaymentService.Api.Endpoints;
using PaymentService.Application.Commands;
using PaymentService.Infrastructure;
using PaymentService.Infrastructure.Data;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("payments", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromSeconds(10);
        limiter.QueueLimit = 0;
    });
});

builder.Host.UseWolverine(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("messaging")!;
    opts.UseAzureServiceBus(connectionString)
        .AutoProvision();

    // Publish outgoing events to their topics
    opts.PublishMessage<PaymentCompletedEvent>().ToAzureServiceBusTopic("payment-events");
    opts.PublishMessage<PaymentFailedEvent>().ToAzureServiceBusTopic("payment-events");

    // Listen to incoming events from other services
    opts.ListenToAzureServiceBusSubscription("order-events/payment-orders-sub");

    // Transactional outbox: persist outgoing messages to SQL Server in the same
    // transaction as the entity write, then dispatch via background flush.
    var paymentsDb = builder.Configuration.GetConnectionString("payments-db")!;
    opts.PersistMessagesWithSqlServer(paymentsDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.Discovery.IncludeAssembly(typeof(ProcessPaymentCommand).Assembly);
    opts.UseFluentValidation();
    opts.Policies.LogMessageStarting(LogLevel.Information);
    opts.AddNextAuroraContextPropagation();
    opts.AddConcurrencyRetry();
});
builder.Services.AddValidatorsFromAssemblyContaining<ProcessPaymentCommand>();
builder.Services.AddPaymentInfrastructure(builder.Configuration);
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
    await app.Services.MigrateDatabaseAsync<PaymentDbContext>();
}

app.UseRateLimiter();
app.MapPaymentEndpoints();
await app.RunAsync();
