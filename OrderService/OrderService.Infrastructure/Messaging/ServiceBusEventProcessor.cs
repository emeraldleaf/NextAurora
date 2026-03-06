using System.Diagnostics;
using System.Diagnostics.Metrics;
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
///   payment-events / order-sub   — receives PaymentCompletedEvent and PaymentFailedEvent,
///                                  dispatching each to the appropriate MediatR handler.
///                                  Dispatching is done by message Subject (the event type
///                                  name, e.g. "PaymentCompletedEvent") so new event types
///                                  can be added without changing deserialization logic.
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
    /// <summary>
    /// Named ActivitySource for Service Bus consumer spans.
    /// When OpenTelemetry is configured (Extensions.cs registers "NextAurora.Messaging"),
    /// StartActivity creates a proper OTel span that appears in the Aspire dashboard, Jaeger,
    /// or Zipkin — connecting inbound message processing to the upstream publisher's trace.
    /// </summary>
    private static readonly ActivitySource _activitySource = new("NextAurora.Messaging");

    /// <summary>
    /// Counter incremented whenever a message is abandoned (routed back for retry or DLQ).
    /// Tagged with the message Subject so dashboards can show which event type is failing.
    /// Alert on this metric rising to detect DLQ pile-ups before they become incidents.
    /// </summary>
    private static readonly Counter<long> _messagesAbandoned =
        new Meter("NextAurora").CreateCounter<long>(
            "messages.abandoned",
            description: "Messages abandoned for retry or dead-letter queue");

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

            // Step 2 — open an OTel span for this message.  _activitySource.StartActivity returns
            //          a real span when OTel is listening; null when it is not (tests/emulator).
            //          The fallback ensures Activity.Current is non-null so baggage writes below
            //          have somewhere to go even without OTel.
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);

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
            bool succeeded = false;
            try
            {
                // Dispatch by Subject (the event type name set by ServiceBusEventPublisher).
                // This pattern is Open/Closed — adding a new event type means adding a new
                // else-if branch without touching any existing deserialization logic.
                var subject = args.Message.Subject;
                if (string.Equals(subject, nameof(PaymentCompletedEvent), StringComparison.Ordinal))
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
                }
                else if (string.Equals(subject, nameof(PaymentFailedEvent), StringComparison.Ordinal))
                {
                    var @event = JsonSerializer.Deserialize<PaymentFailedEvent>(args.Message.Body.ToString());
                    if (@event is not null)
                    {
                        using var serviceScope = serviceProvider.CreateScope();
                        var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                        await mediator.Publish(new PaymentFailedNotification(@event), stoppingToken);
                    }
                }
                else
                {
                    // Unknown subject — log and complete rather than abandon, to avoid infinite
                    // DLQ retries for messages that can never be processed by this subscription.
                    logger.LogWarning("Received unrecognised message subject '{Subject}' on payment-events. Completing without processing.", subject);
                }

                // Completing the message removes it from the subscription.
                // Only call this after all work is done — if we complete early and then throw,
                // the order update is silently lost.
                succeeded = true;
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                if (!succeeded)
                {
                    // Abandoning returns the message to the subscription for retry.
                    // The SDK increments DeliveryCount each time.  When DeliveryCount exceeds the
                    // subscription's MaxDeliveryCount the broker moves it to the Dead Letter Queue.
                    _messagesAbandoned.Add(1,
                        new KeyValuePair<string, object?>("subject", args.Message.Subject),
                        new KeyValuePair<string, object?>("service", "OrderService"));
                    logger.LogError(ex, "Failed to process payment event after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
                }
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
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            bool succeeded = false;
            try
            {
                var @event = JsonSerializer.Deserialize<ShipmentDispatchedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new ShipmentDispatchedNotification(@event), stoppingToken);
                }
                succeeded = true;
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                if (!succeeded)
                {
                    _messagesAbandoned.Add(1,
                        new KeyValuePair<string, object?>("subject", args.Message.Subject),
                        new KeyValuePair<string, object?>("service", "OrderService"));
                    logger.LogError(ex, "Failed to process ShipmentDispatched event after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
                }
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
