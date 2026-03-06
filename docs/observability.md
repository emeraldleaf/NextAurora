# Observability & Debuggability Guide

This document describes the observability features added to NextAurora, how they work together, and how to use them when debugging production issues.

---

## Overview

Every request or event in NextAurora now carries a **Correlation ID** that flows from the initial HTTP call through every Service Bus message and log line across all services. Combined with OpenTelemetry distributed tracing, structured logging, MediatR pipeline telemetry, business metrics, and Dead Letter Queue (DLQ) handling, this gives you a complete picture of any transaction — even when it spans five microservices.

---

## Correlation ID Propagation

### How It Works

`CorrelationIdMiddleware` (registered globally in `ServiceDefaults`) runs on every HTTP request:

1. Reads the `X-Correlation-Id` request header. If absent, generates one from the active W3C trace ID or a new GUID.
2. Stores it in `Activity` baggage (`correlation.id`) so it is automatically carried through any downstream HTTP or gRPC calls via W3C TraceContext propagation.
3. Opens an `ILogger` scope enriched with `CorrelationId`, so every log line written during that request automatically includes the value.
4. Echoes the ID in the `X-Correlation-Id` response header so clients can record it.

### Propagation Through Service Bus

When a service publishes an event, the publisher injects the correlation ID into the message:

```csharp
message.ApplicationProperties["X-Correlation-Id"] = correlationId;
message.CorrelationId = correlationId;  // also visible in Azure portal
```

When a processor receives the message, it extracts the correlation ID and opens a logging scope before dispatching:

```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["CorrelationId"] = correlationId,
    ["MessageId"]     = args.Message.MessageId,
    ["Subject"]       = args.Message.Subject,
    ["DeliveryCount"] = args.Message.DeliveryCount
});
```

Every log line written by any handler invoked from that processor will carry all four fields.

### Finding a Transaction

Given a correlation ID (from a client error report or response header), you can retrieve the entire cross-service trace in any structured log sink:

```
CorrelationId = "a3f1b2c4..."
```

This returns every log line — HTTP request, MediatR handler, Service Bus publish, Service Bus receive, and notification send — for that single transaction.

For the full three-identifier guide (UserId, SessionId, new-service checklist, common pitfalls), see **[docs/context-propagation.md](context-propagation.md)**.

---

## Distributed Tracing (OpenTelemetry)

### What Is Traced

`ServiceDefaults` configures OpenTelemetry tracing with the following sources:

| Source | What It Covers |
|--------|----------------|
| `{ServiceName}` (application name) | Custom spans per service |
| `Azure.Messaging.ServiceBus` | Service Bus send/receive/complete/abandon operations |
| ASP.NET Core instrumentation | Inbound HTTP requests |
| gRPC client instrumentation | OrderService → CatalogService gRPC calls |
| HTTP client instrumentation | All outbound HTTP calls |

Health check endpoints (`/health`, `/alive`) are excluded from traces to reduce noise.

### Viewing Traces

When `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, all traces are exported via OTLP. In local development with Aspire, traces are visible in the Aspire dashboard. In production, connect any OTLP-compatible backend (Jaeger, Tempo, Azure Monitor, etc.).

A single trace for an order placement will show spans across:

```
[OrderService.Api] POST /orders
  └─ [OrderService] PlaceOrderCommand handler
       └─ [CatalogService gRPC] GetProduct / ReserveStock
       └─ [Azure.Messaging.ServiceBus] Send → order-events
            └─ [PaymentService] OrderPlaced processor
                 └─ [PaymentService] ProcessPayment handler
                      └─ [Azure.Messaging.ServiceBus] Send → payment-events
                           └─ [ShippingService] PaymentCompleted processor
                           └─ [NotificationService] OrderPlaced processor
```

---

## MediatR Pipeline Logging

Every command and query in Order, Payment, Catalog, and Shipping services is wrapped by `LoggingBehavior<TRequest, TResponse>`, which runs in the MediatR pipeline after `ValidationBehavior`.

For each handler execution it logs:

- **Start**: handler name + correlation ID
- **End** (`finally` block): handler name, elapsed milliseconds, outcome (success / warning on failure)

Example log output:

```
[INF] Handling PlaceOrderCommand (CorrelationId: a3f1b2c4...)
[INF] Handled PlaceOrderCommand in 142ms (CorrelationId: a3f1b2c4...)
```

On failure:

```
[INF] Handling PlaceOrderCommand (CorrelationId: a3f1b2c4...)
[WRN] Failed PlaceOrderCommand after 38ms (CorrelationId: a3f1b2c4...)
```

The exception itself is handled and logged by `GlobalExceptionHandler` in `ServiceDefaults`, which formats it as a `ProblemDetails` response including the trace ID.

---

## Dead Letter Queue (DLQ) Handling

Previously, all Service Bus processors silently discarded failures after logging. Now, on any unhandled exception during message processing, the processor calls:

```csharp
await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
```

Azure Service Bus then increments the message's **DeliveryCount**. Once `DeliveryCount` reaches the queue/subscription's configured `MaxDeliveryCount`, the message is automatically moved to the **Dead Letter Queue** for that entity.

The `DeliveryCount` is always logged in the structured scope, so you can see retry progress:

```
[ERR] Failed to process OrderPlaced event. Abandoning for retry/DLQ
      CorrelationId=a3f1b2c4, MessageId=abc-123, Subject=OrderPlacedEvent, DeliveryCount=2
```

### DLQ Entities

| Service Bus Entity | DLQ Path |
|--------------------|----------|
| `order-events / payment-sub` | `order-events/Subscriptions/payment-sub/$deadletterqueue` |
| `order-events / notify-sub` | `order-events/Subscriptions/notify-sub/$deadletterqueue` |
| `payment-events / order-sub` | `payment-events/Subscriptions/order-sub/$deadletterqueue` |
| `payment-events / shipping-sub` | `payment-events/Subscriptions/shipping-sub/$deadletterqueue` |
| `shipping-events / order-sub` | `shipping-events/Subscriptions/order-sub/$deadletterqueue` |
| `shipping-events / notify-sub` | `shipping-events/Subscriptions/notify-sub/$deadletterqueue` |
| `send-notification` (queue) | `send-notification/$deadletterqueue` |

### Investigating a DLQ Message

1. In the Azure portal, navigate to the Service Bus namespace → topic/queue → subscription → Dead-letter.
2. Peek or receive the message.
3. Check `ApplicationProperties["X-Correlation-Id"]` to retrieve the original correlation ID.
4. Search your log sink with that ID to see the full history of attempts.
5. Fix the root cause, then replay the message by receiving it from the DLQ and re-publishing it to the original topic/queue.

### Transport Errors

`ProcessErrorAsync` on each processor now logs structured fields for infrastructure-level errors (disconnects, auth failures):

```
[ERR] Service Bus transport error on order-events/Subscriptions/payment-sub
      ErrorSource=Receive, FullyQualifiedNamespace=nextaurora.servicebus.windows.net
```

---

## Business Metrics

A `Meter("NextAurora")` is registered in `ServiceDefaults` and collected by the OpenTelemetry metrics pipeline. The following counters are incremented by the relevant handlers:

| Metric Name | Incremented By | Tags |
|-------------|----------------|------|
| `orders.placed` | `PlaceOrderHandler` | — |
| `payments.processed` | `ProcessPaymentHandler` | `outcome=success\|failed` |
| `shipments.dispatched` | `CreateShipmentHandler` | — |
| `notifications.sent` | `SendNotificationHandler` | `channel=Email\|…` |
| `messages.abandoned` | All service processors | `subject=<EventType>`, `service=<ServiceName>` |

These are available in the Aspire dashboard under **Metrics** in development. In production, they are exported via OTLP to your metrics backend (Prometheus, Azure Monitor, etc.).

---

## Database Health Checks

Each service registers an EF Core health check for its database:

| Service | DbContext | Connection String Key |
|---------|-----------|----------------------|
| OrderService | `OrderDbContext` | `orders-db` |
| PaymentService | `PaymentDbContext` | `payments-db` |
| CatalogService | `CatalogDbContext` | `catalog-db` |
| ShippingService | `ShippingDbContext` | `shipping-db` |

Health check endpoints are now available in all environments (not just development):

- `GET /health` — all registered checks must pass (readiness probe)
- `GET /alive` — only the `live`-tagged self check (liveness probe)

A failing database health check returns HTTP 503, allowing Kubernetes or Aspire to route traffic away from the unhealthy instance.

---

## Files Added / Modified

### New Files

| File | Purpose |
|------|---------|
| `NextAurora.ServiceDefaults/Middleware/CorrelationIdMiddleware.cs` | HTTP correlation ID propagation |
| `NextAurora.ServiceDefaults/Metrics/NovaCraftMetrics.cs` | Business metrics counters |
| `OrderService.Application/Behaviors/LoggingBehavior.cs` | MediatR pipeline logging |
| `PaymentService.Application/Behaviors/LoggingBehavior.cs` | MediatR pipeline logging |
| `CatalogService.Application/Behaviors/LoggingBehavior.cs` | MediatR pipeline logging |
| `ShippingService.Application/Behaviors/LoggingBehavior.cs` | MediatR pipeline logging |

### Modified Files

| File | Change |
|------|--------|
| `NextAurora.ServiceDefaults/Extensions.cs` | Register middleware; add Azure SB + NextAurora meter sources; enable health checks in all environments |
| `Directory.Packages.props` | Added `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.2` |
| `{Order,Payment,Shipping}Service.Infrastructure/Messaging/ServiceBusEventPublisher.cs` | Inject correlation ID into outbound messages |
| `OrderService.Infrastructure/Messaging/ServiceBusEventProcessor.cs` | Structured logging scope + AbandonMessageAsync on failure |
| `PaymentService.Infrastructure/Messaging/OrderPlacedProcessor.cs` | Structured logging scope + AbandonMessageAsync on failure |
| `ShippingService.Infrastructure/Messaging/PaymentCompletedProcessor.cs` | Structured logging scope + AbandonMessageAsync on failure |
| `NotificationService.Infrastructure/Messaging/EventProcessor.cs` | Structured logging scope + AbandonMessageAsync on failure |
| `{Order,Payment,Catalog,Shipping}Service.Infrastructure/DependencyInjection.cs` | Added `AddDbContextCheck<T>()` |
| `{Order,Payment,Catalog,Shipping}Service.Infrastructure/*.csproj` | Added EF Core health checks package reference |
| `{Payment,Catalog,Shipping}Service.Application/*.csproj` | Added `Microsoft.Extensions.Logging.Abstractions` |
| `{Order,Payment,Catalog,Shipping}Service.Api/Program.cs` | Registered `LoggingBehavior` in MediatR pipeline |
| `OrderService.Application/Handlers/PlaceOrderHandler.cs` | Increments `orders.placed` counter |
| `PaymentService.Application/Handlers/ProcessPaymentHandler.cs` | Increments `payments.processed` counter with outcome tag |
| `ShippingService.Application/Handlers/CreateShipmentHandler.cs` | Increments `shipments.dispatched` counter |
| `NotificationService.Application/Commands/SendNotificationHandler.cs` | Increments `notifications.sent` counter with channel tag |

---

## Event Replay

Every published event is persisted to an `EventLogs` table before it is sent to Service Bus. Admin endpoints allow querying history and replaying events by ID or correlation ID.

See **[docs/event-replay.md](event-replay.md)** for the full guide: schema, `LoggingEventPublisher` decorator, endpoint reference, configuration, and migration commands.
