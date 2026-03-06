using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using PaymentService.Application.EventHandlers;

namespace PaymentService.Infrastructure.Messaging;

public class OrderPlacedProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<OrderPlacedProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = client.CreateProcessor("order-events", "payment-sub");

        processor.ProcessMessageAsync += async args =>
        {
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString() : args.Message.CorrelationId;
            var userId = args.Message.ApplicationProperties.TryGetValue("X-User-Id", out var uid)
                ? uid?.ToString() : null;
            var sessionId = args.Message.ApplicationProperties.TryGetValue("X-Session-Id", out var sid)
                ? sid?.ToString() : null;

            using var processorActivity = Activity.Current is null
                ? new Activity("ServiceBus.ProcessMessage").Start() : null;
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = correlationId,
                ["MessageId"] = args.Message.MessageId,
                ["Subject"] = args.Message.Subject,
                ["DeliveryCount"] = args.Message.DeliveryCount
            };
            if (userId is not null) scopeState["UserId"] = userId;
            if (sessionId is not null) scopeState["SessionId"] = sessionId;

            using var scope = logger.BeginScope(scopeState);
            try
            {
                var @event = JsonSerializer.Deserialize<OrderPlacedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new OrderPlacedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process OrderPlaced event. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
