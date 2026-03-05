using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaCraft.Contracts.Events;
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
        paymentProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on payment-events");
            return Task.CompletedTask;
        };

        var shippingProcessor = client.CreateProcessor("shipping-events", "order-sub");
        shippingProcessor.ProcessMessageAsync += async args =>
        {
            try
            {
                var @event = JsonSerializer.Deserialize<ShipmentDispatchedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var scope = serviceProvider.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new ShipmentDispatchedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing ShipmentDispatched event");
            }
        };
        shippingProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on shipping-events");
            return Task.CompletedTask;
        };

        await paymentProcessor.StartProcessingAsync(stoppingToken);
        await shippingProcessor.StartProcessingAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
