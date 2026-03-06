using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Application.EventHandlers;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Background service that subscribes to two Service Bus topics and drives OrderService's
/// reaction to events published by other services:
///
///   payment-events / order-sub   — receives PaymentCompletedEvent (or PaymentFailedEvent)
///                                  and updates the order's payment status.
///   shipping-events / order-sub  — receives ShipmentDispatchedEvent and marks the order
///                                  as shipped.
///
/// Message processing lifecycle:
///   1. The Azure SDK calls ProcessMessageAsync on a thread-pool thread for each message.
///   2. We restore observability context (see ExtractContext / activity guard below).
///   3. We dispatch to MediatR so domain logic stays in the Application layer.
///   4. On success we call CompleteMessageAsync — the message is removed from the topic.
///   5. On any exception we call AbandonMessageAsync — the delivery count increments and
///      the Service Bus retries delivery (up to the configured max). After all retries are
///      exhausted the broker moves the message to the Dead Letter Queue for investigation.
///
/// Why BackgroundService?
///   BackgroundService is the standard .NET hosted service base class.  ExecuteAsync runs
///   once when the host starts; Task.Delay(Infinite) keeps it alive until the host shuts down.
///   The CancellationToken (stoppingToken) is signalled on graceful shutdown.
///
/// Why IServiceProvider / CreateScope?
///   The processors are singleton-lifetime BackgroundServices but DbContext (used by handlers)
///   is scoped.  Creating a new DI scope per message is the correct way to resolve scoped
///   services from a singleton — without it you'd get a "cannot consume scoped service from
///   singleton" exception at startup.
/// </summary>
public class ServiceBusEventProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<ServiceBusEventProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CreateProcessor attaches to a subscription on a topic.
        // The subscription ("order-sub") is pre-created in Azure and filters messages
        // intended for this service — other services have their own subscriptions on the same topic.
        var paymentProcessor = client.CreateProcessor("payment-events", "order-sub");
        paymentProcessor.ProcessMessageAsync += async args =>
        {
            // Step 1 — recover the three context identifiers that were stamped onto the message
            //          by the publishing service's ServiceBusEventPublisher.
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);

            // Step 2 — ensure an Activity exists before setting baggage.
            //          The Azure SDK creates an Activity automatically when OpenTelemetry is
            //          configured (production).  In other environments (Service Bus emulator,
            //          unit tests) Activity.Current is null, so we create a short-lived one.
            //          'using' ensures it is disposed when this handler completes.
            using var processorActivity = Activity.Current is null
                ? new Activity("ServiceBus.ProcessMessage").Start() : null;

            // Step 3 — write the IDs into Activity baggage so that any code called from here
            //          (e.g. LoggingBehavior inside MediatR) can read them without being passed
            //          them explicitly.
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            // Step 4 — open a structured log scope so every log line written in this handler
            //          (and all code it calls) automatically includes CorrelationId, MessageId,
            //          Subject, DeliveryCount, UserId, and SessionId.
            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                var @event = JsonSerializer.Deserialize<PaymentCompletedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    // Create a DI scope per message so scoped services (DbContext etc.) are
                    // correctly lifetime-managed and not shared between concurrent messages.
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new PaymentCompletedNotification(@event), stoppingToken);
                }
                // Completing the message removes it from the subscription.
                // Only call this after all work is done — if we complete early and then throw,
                // the order update is silently lost.
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                // Abandoning returns the message to the subscription for retry.
                // The SDK increments DeliveryCount each time.  When DeliveryCount exceeds the
                // subscription's MaxDeliveryCount the broker moves it to the Dead Letter Queue.
                logger.LogError(ex, "Failed to process PaymentCompleted event. Abandoning for retry/DLQ");
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };

        // Transport-level errors (network failures, authentication issues) are surfaced here.
        // These are distinct from handler exceptions — they indicate the SDK could not even
        // deliver the message to our code.
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
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            using var processorActivity = Activity.Current is null
                ? new Activity("ServiceBus.ProcessMessage").Start() : null;
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

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

        // Keep ExecuteAsync alive for the lifetime of the host.
        // The SDK processes messages on its own thread pool — this thread just waits.
        // stoppingToken is cancelled on graceful shutdown, causing Delay to throw
        // OperationCanceledException which the host handles cleanly.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Reads the three observability identifiers from a message's ApplicationProperties.
    /// Falls back to the SDK-level CorrelationId field for services that set that field
    /// instead of (or in addition to) the custom X-Correlation-Id property.
    /// </summary>
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

    /// <summary>
    /// Builds the dictionary passed to logger.BeginScope().
    /// Including MessageId, Subject, and DeliveryCount alongside the context IDs makes it
    /// trivial to find all log lines for a specific message and to spot retry storms
    /// (DeliveryCount climbing) in the log output.
    /// StringComparer.Ordinal is required to satisfy the MA0002 analyzer rule.
    /// </summary>
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
