using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using ShippingService.Application.EventHandlers;

namespace ShippingService.Infrastructure.Messaging;

/// <summary>
/// Background service that subscribes to the "payment-events" topic and creates a shipment
/// whenever a PaymentCompletedEvent arrives.
///
/// Subscription: payment-events / shipping-sub
///   Shipping and OrderService each have their own subscription on payment-events.
///   Both receive the same message independently; completing or failing in one subscription
///   has no effect on the other.
///
/// Idempotency:
///   Service Bus guarantees at-least-once delivery — the same message may arrive more than once
///   (e.g. after a network interruption between CompleteMessageAsync and the broker's ACK).
///   Shipment creation handlers should check whether a shipment for the given order already
///   exists before creating a new one to prevent duplicate shipments.
///
/// Context propagation:
///   CorrelationId, UserId, and SessionId are extracted from ApplicationProperties and restored
///   into Activity baggage and logger scope.  See docs/context-propagation.md for the full flow.
///
/// Scoped services:
///   BackgroundService is singleton; DbContext (PostgreSQL via EF Core) is scoped.
///   A new DI scope is created per message to give each handler its own DbContext instance.
/// </summary>
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
            // Extract observability context stamped by ServiceBusEventPublisher in PaymentService.
            var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
                ? cid?.ToString() : args.Message.CorrelationId;
            var userId = args.Message.ApplicationProperties.TryGetValue("X-User-Id", out var uid)
                ? uid?.ToString() : null;
            var sessionId = args.Message.ApplicationProperties.TryGetValue("X-Session-Id", out var sid)
                ? sid?.ToString() : null;

            // Guard against null Activity (emulator / test environment).
            // 'using' disposes the activity (if we created one) when the handler exits.
            using var processorActivity = Activity.Current is null
                ? new Activity("ServiceBus.ProcessMessage").Start() : null;
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            // Structured log scope — all log lines inside this block carry these fields.
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
