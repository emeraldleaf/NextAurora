using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Commands;
using NextAurora.Contracts.Events;
using NotificationService.Application.Commands;
using NotificationService.Application.EventHandlers;

namespace NotificationService.Infrastructure.Messaging;

public class EventProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<EventProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var orderProcessor = client.CreateProcessor("order-events", "notify-sub");
        orderProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            SetActivityBaggage(correlationId, userId, sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
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
                logger.LogError(ex, "Failed to process OrderPlaced event for notification. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        orderProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        var shippingProcessor = client.CreateProcessor("shipping-events", "notify-sub");
        shippingProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            SetActivityBaggage(correlationId, userId, sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                var @event = JsonSerializer.Deserialize<ShipmentDispatchedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new ShipmentDispatchedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process ShipmentDispatched event for notification. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        shippingProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        var queueProcessor = client.CreateProcessor("send-notification");
        queueProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            SetActivityBaggage(correlationId, userId, sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                var command = JsonSerializer.Deserialize<SendNotificationCommand>(args.Message.Body.ToString());
                if (command is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Send(new SendNotificationRequest(
                        command.RecipientId, command.RecipientEmail,
                        command.Subject, command.Body, command.Channel), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process send-notification command. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        queueProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        await orderProcessor.StartProcessingAsync(stoppingToken);
        await shippingProcessor.StartProcessingAsync(stoppingToken);
        await queueProcessor.StartProcessingAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static (string? correlationId, string? userId, string? sessionId) ExtractContext(ServiceBusReceivedMessage message)
    {
        var correlationId = message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
            ? cid?.ToString() : message.CorrelationId;
        var userId = message.ApplicationProperties.TryGetValue("X-User-Id", out var uid)
            ? uid?.ToString() : null;
        var sessionId = message.ApplicationProperties.TryGetValue("X-Session-Id", out var sid)
            ? sid?.ToString() : null;
        return (correlationId, userId, sessionId);
    }

    private static void SetActivityBaggage(string? correlationId, string? userId, string? sessionId)
    {
        if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
        if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
        if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);
    }

    private static Dictionary<string, object?> BuildScope(string? correlationId, string? userId, string? sessionId, ServiceBusReceivedMessage message)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId,
            ["MessageId"] = message.MessageId,
            ["Subject"] = message.Subject,
            ["DeliveryCount"] = message.DeliveryCount
        };
        if (userId is not null) scope["UserId"] = userId;
        if (sessionId is not null) scope["SessionId"] = sessionId;
        return scope;
    }
}
