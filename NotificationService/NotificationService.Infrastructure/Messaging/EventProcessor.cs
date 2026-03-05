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
        // Subscribe to order-events
        var orderProcessor = client.CreateProcessor("order-events", "notify-sub");
        orderProcessor.ProcessMessageAsync += async args =>
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
                logger.LogError(ex, "Error processing OrderPlaced event for notification");
            }
        };
        orderProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on order-events/notify-sub");
            return Task.CompletedTask;
        };

        // Subscribe to shipping-events
        var shippingProcessor = client.CreateProcessor("shipping-events", "notify-sub");
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
                logger.LogError(ex, "Error processing ShipmentDispatched event for notification");
            }
        };
        shippingProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on shipping-events/notify-sub");
            return Task.CompletedTask;
        };

        // Consume send-notification queue
        var queueProcessor = client.CreateProcessor("send-notification");
        queueProcessor.ProcessMessageAsync += async args =>
        {
            try
            {
                var command = JsonSerializer.Deserialize<SendNotificationCommand>(args.Message.Body.ToString());
                if (command is not null)
                {
                    using var scope = serviceProvider.CreateScope();
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Send(new SendNotificationRequest(
                        command.RecipientId, command.RecipientEmail,
                        command.Subject, command.Body, command.Channel), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing send-notification command");
            }
        };
        queueProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error on send-notification queue");
            return Task.CompletedTask;
        };

        await orderProcessor.StartProcessingAsync(stoppingToken);
        await shippingProcessor.StartProcessingAsync(stoppingToken);
        await queueProcessor.StartProcessingAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
