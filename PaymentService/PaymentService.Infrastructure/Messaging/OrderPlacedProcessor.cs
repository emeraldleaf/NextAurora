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
            try
            {
                var @event = JsonSerializer.Deserialize<OrderPlacedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var scope = serviceProvider.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new OrderPlacedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing OrderPlaced event");
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on order-events/payment-sub");
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
