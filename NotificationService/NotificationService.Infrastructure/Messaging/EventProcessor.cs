using System.Diagnostics;
using System.Diagnostics.Metrics;
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

/// <summary>
/// Background service that drives all of NotificationService's inbound message processing.
/// It subscribes to three sources simultaneously:
///
///   order-events / notify-sub    — OrderPlacedEvent  → "Order Received" email/push
///   shipping-events / notify-sub — ShipmentDispatchedEvent → "Order Shipped" email/push
///   send-notification (queue)    — SendNotificationCommand → direct notification request
///                                  (other services can enqueue a notification without knowing
///                                   the notification channel or template details)
///
/// Topics vs Queues:
///   Topics (order-events, shipping-events) support multiple subscriptions — many services
///   receive the same message independently.  A Queue (send-notification) is point-to-point:
///   only one consumer receives each message, and there are no subscriptions.
///
/// All three processors start concurrently and run in parallel for the lifetime of the host.
///
/// Context propagation, DI scoping, and complete/abandon semantics follow the same pattern
/// as the other service processors.  See docs/context-propagation.md for the full picture.
/// </summary>
public class EventProcessor(
    ServiceBusClient client,
    IServiceProvider serviceProvider,
    ILogger<EventProcessor> logger) : BackgroundService
{
    private static readonly ActivitySource _activitySource = new("NextAurora.Messaging");

    private static readonly Counter<long> _messagesAbandoned =
        new Meter("NextAurora").CreateCounter<long>(
            "messages.abandoned",
            description: "Messages abandoned for retry or dead-letter queue");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── order-events / notify-sub ────────────────────────────────────────────────────────
        var orderProcessor = client.CreateProcessor("order-events", "notify-sub");
        orderProcessor.ProcessMessageAsync += async args =>
        {
            // Restore the three observability IDs from the message ApplicationProperties.
            // These were stamped by ServiceBusEventPublisher in OrderService.
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);

            // Ensure an Activity exists so baggage writes below don't silently no-op.
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                var @event = JsonSerializer.Deserialize<OrderPlacedEvent>(args.Message.Body.ToString());
                if (@event is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Publish(new OrderPlacedNotification(@event), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                _messagesAbandoned.Add(1,
                    new KeyValuePair<string, object?>("subject", args.Message.Subject),
                    new KeyValuePair<string, object?>("service", "NotificationService"));
                logger.LogError(ex, "Failed to process OrderPlaced event for notification after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        orderProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        // ── shipping-events / notify-sub ─────────────────────────────────────────────────────
        var shippingProcessor = client.CreateProcessor("shipping-events", "notify-sub");
        shippingProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
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
                _messagesAbandoned.Add(1,
                    new KeyValuePair<string, object?>("subject", args.Message.Subject),
                    new KeyValuePair<string, object?>("service", "NotificationService"));
                logger.LogError(ex, "Failed to process ShipmentDispatched event for notification after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
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

        // ── send-notification queue ───────────────────────────────────────────────────────────
        // This is a direct command queue, not a topic subscription.
        // Any service can enqueue a SendNotificationCommand without knowing which channel
        // (email, push, SMS) will be used — that decision lives inside NotificationService.
        var queueProcessor = client.CreateProcessor("send-notification");
        queueProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                var command = JsonSerializer.Deserialize<SendNotificationCommand>(args.Message.Body.ToString());
                if (command is not null)
                {
                    using var serviceScope = serviceProvider.CreateScope();
                    var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                    await mediator.Send(new SendNotificationRequest(
                        command.RecipientId, command.RecipientEmail,
                        command.Subject, command.Body, command.Channel), stoppingToken);
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                _messagesAbandoned.Add(1,
                    new KeyValuePair<string, object?>("subject", args.Message.Subject),
                    new KeyValuePair<string, object?>("service", "NotificationService"));
                logger.LogError(ex, "Failed to process send-notification command after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
                await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            }
        };
        queueProcessor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception,
                "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
                args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
            return Task.CompletedTask;
        };

        // Start all three processors concurrently.  Each runs on the Service Bus SDK's
        // thread pool independently — a slow notification does not delay an order event.
        await orderProcessor.StartProcessingAsync(stoppingToken);
        await shippingProcessor.StartProcessingAsync(stoppingToken);
        await queueProcessor.StartProcessingAsync(stoppingToken);

        // ── payment-events / notify-sub ──────────────────────────────────────────────────────
        // Subscribes to payment results so we can send "Payment Failed" emails.
        // The subscription "notify-sub" on payment-events must be provisioned in Azure
        // alongside the "order-sub" and "shipping-sub" subscriptions on the same topic.
        var paymentProcessor = client.CreateProcessor("payment-events", "notify-sub");
        paymentProcessor.ProcessMessageAsync += async args =>
        {
            var (correlationId, userId, sessionId) = ExtractContext(args.Message);
            using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
                ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
            if (correlationId is not null) Activity.Current?.SetBaggage("correlation.id", correlationId);
            if (userId is not null) Activity.Current?.SetBaggage("user.id", userId);
            if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

            using var scope = logger.BeginScope(BuildScope(correlationId, userId, sessionId, args.Message));
            try
            {
                // Only PaymentFailedEvent warrants a notification; PaymentCompletedEvent is
                // handled by the shipping flow which eventually triggers a "shipped" email.
                if (string.Equals(args.Message.Subject, nameof(PaymentFailedEvent), StringComparison.Ordinal))
                {
                    var @event = JsonSerializer.Deserialize<PaymentFailedEvent>(args.Message.Body.ToString());
                    if (@event is not null)
                    {
                        using var serviceScope = serviceProvider.CreateScope();
                        var mediator = serviceScope.ServiceProvider.GetRequiredService<IMediator>();
                        await mediator.Publish(new PaymentFailedNotification(@event), stoppingToken);
                    }
                }
                await args.CompleteMessageAsync(args.Message, stoppingToken);
            }
            catch (Exception ex)
            {
                _messagesAbandoned.Add(1,
                    new KeyValuePair<string, object?>("subject", args.Message.Subject),
                    new KeyValuePair<string, object?>("service", "NotificationService"));
                logger.LogError(ex, "Failed to process payment event for notification after {DeliveryCount} attempt(s). Abandoning for retry/DLQ", args.Message.DeliveryCount);
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
        await paymentProcessor.StartProcessingAsync(stoppingToken);

        // Keep ExecuteAsync alive until the host shuts down.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Reads CorrelationId, UserId, and SessionId from the message's ApplicationProperties.
    /// Falls back to the SDK-level CorrelationId field for the correlation ID in case the
    /// publisher only set that (rather than the custom X-Correlation-Id property).
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
    /// Builds the logger scope dictionary.  All fields in this dictionary are automatically
    /// appended to every structured log line written while the scope is open.
    /// DeliveryCount is particularly useful — a value above 1 means this message has been
    /// retried, which can indicate a bug in the handler or a transient infrastructure issue.
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
