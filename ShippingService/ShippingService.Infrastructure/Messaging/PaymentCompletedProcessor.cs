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
            try
            {
                var @event = JsonSerializer.Deserialize<PaymentCompletedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var scope = serviceProvider.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new PaymentCompletedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing PaymentCompleted event");
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on payment-events/shipping-sub");
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
