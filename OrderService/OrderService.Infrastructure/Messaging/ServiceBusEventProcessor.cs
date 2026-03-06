using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Application.EventHandlers;

namespace OrderService.Infrastructure.Messaging;

public class ServiceBusEventProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<ServiceBusEventProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paymentProcessor = client.CreateProcessor("payment-events", "order-sub");
        paymentProcessor.ProcessMessageAsync += async args =>
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
                var @event = JsonSerializer.Deserialize<PaymentCompletedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new PaymentCompletedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process PaymentCompleted event. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        paymentProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        var shippingProcessor = client.CreateProcessor("shipping-events", "order-sub");
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
                logger.LogError(ex, "Failed to process ShipmentDispatched event. Abandoning for retry/DLQ");
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

        await paymentProcessor.StartProcessingAsync(stoppingToken);
        await shippingProcessor.StartProcessingAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
