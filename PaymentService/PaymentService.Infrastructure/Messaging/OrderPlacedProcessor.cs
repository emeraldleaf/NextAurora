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

/// <summary>
/// Background service that subscribes to the "order-events" topic and triggers payment
/// processing whenever an OrderPlacedEvent arrives.
///
/// Subscription: order-events / payment-sub
///   Each service that cares about order-events has its own subscription.
///   PaymentService uses "payment-sub"; NotificationService uses "notify-sub".
///   They receive independent copies of the same message — one service completing or failing
///   has no effect on the other.
///
/// Message processing lifecycle (complete / abandon / dead-letter):
///   CompleteMessageAsync — work succeeded; broker removes the message.
///   AbandonMessageAsync  — work failed; broker re-queues for retry.
///                          After MaxDeliveryCount attempts the broker moves it to the
///                          Dead Letter Queue where it can be inspected and replayed.
///
/// Context propagation (see docs/context-propagation.md for the full picture):
///   The publishing service embedded CorrelationId, UserId, and SessionId as
///   ApplicationProperties on the message.  We extract them here and restore them into
///   Activity baggage and logger scope so all downstream log lines carry those IDs —
///   exactly as if this were a new HTTP request.
///
/// Scoped services:
///   BackgroundService is singleton; DbContext (used inside payment handlers) is scoped.
///   A new DI scope is created per message to give each handler its own DbContext instance.
/// </summary>
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
            // Extract the three observability IDs from the message's ApplicationProperties.
            // These were written by ServiceBusEventPublisher in OrderService.
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString() : args.Message.CorrelationId;
            var userId = args.Message.ApplicationProperties.TryGetValue("X-User-Id", out var uid)
                ? uid?.ToString() : null;
            var sessionId = args.Message.ApplicationProperties.TryGetValue("X-Session-Id", out var sid)
                ? sid?.ToString() : null;

            // Ensure an Activity exists before setting baggage.
            // The Azure SDK creates one automatically when OTel is configured.
            // In other environments (emulator, tests) Activity.Current is null — we create a
            // short-lived one so baggage calls below don't silently no-op.
            using var processorActivity = Activity.Current is null
                ? new Activity("ServiceBus.ProcessMessage").Start() : null;
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            // Open the structured log scope.  Every log line emitted below — including inside
            // MediatR handlers — will automatically carry these fields.
            // DeliveryCount is included so retry storms (count climbing) are visible in logs.
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
                    // New DI scope per message — required because DbContext is scoped (not singleton).
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new OrderPlacedNotification(@event), stoppingToken);
                }
                // Only complete the message after all work succeeds.
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                // Abandon for retry.  The broker will redeliver after a configurable delay.
                // If MaxDeliveryCount is reached the message moves to the Dead Letter Queue.
                logger.LogError(ex, "Failed to process OrderPlaced event. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };

        // Transport errors (disconnections, auth failures) are separate from message-processing
        // errors.  Log them but there is nothing to complete/abandon — the SDK handles reconnect.
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        // Keep this method alive. The SDK processes messages on background threads.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
