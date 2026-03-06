using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Application.EventHandlers;

namespace ShippingService.Infrastructure.Messaging;

public class PaymentCompletedProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<PaymentCompletedProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = client.CreateProcessor("payment-events", "shipping-sub");

        processor.ProcessMessageAsync += async args =>
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
