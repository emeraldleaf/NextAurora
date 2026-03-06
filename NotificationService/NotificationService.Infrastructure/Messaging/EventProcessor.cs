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
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString()
                : args.Message.CorrelationId;

            using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = correlationId,
                ["MessageId"] = args.Message.MessageId,
                ["Subject"] = args.Message.Subject,
                ["DeliveryCount"] = args.Message.DeliveryCount
            });

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
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString()
                : args.Message.CorrelationId;

            using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = correlationId,
                ["MessageId"] = args.Message.MessageId,
                ["Subject"] = args.Message.Subject,
                ["DeliveryCount"] = args.Message.DeliveryCount
            });

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
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString()
                : args.Message.CorrelationId;

            using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = correlationId,
                ["MessageId"] = args.Message.MessageId,
                ["Subject"] = args.Message.Subject,
                ["DeliveryCount"] = args.Message.DeliveryCount
            });

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
}
