# Event-Driven Observability & Debuggability

This document describes the observability, context propagation, and reliability features built into NextAurora to make the event-driven architecture debuggable and production-ready.

---

## Why This Matters

In a microservices system, a single user action ("Buy") fans out into a chain of events across multiple services — order placement, payment processing, shipment creation, and notifications. Without deliberate observability infrastructure, diagnosing failures means:

- No single trail to follow across service boundaries
- Silent failures when a message consumer crashes
- Errors that surface minutes later in a completely different service
- Log output scrambled by retries, replays, and out-of-order delivery

The features below give every team member a clear view of what happened, when, and why.

---

## Context Propagation

### The Three Identifiers

Every request, message, and log line in NextAurora carries three identifiers:

| Identifier | Purpose | HTTP Header | Service Bus Property | Logger Scope Key |
|---|---|---|---|---|
| Correlation ID | Links all events in one user transaction | `X-Correlation-Id` | `X-Correlation-Id` | `CorrelationId` |
| User ID | The authenticated user who triggered the chain | `X-User-Id` | `X-User-Id` | `UserId` |
| Session ID | Browser/app session for cross-request grouping | `X-Session-Id` | `X-Session-Id` | `SessionId` |

### How They Flow

```
Browser Request
  → CorrelationIdMiddleware (NextAurora.ServiceDefaults)
      generates CorrelationId, reads UserId/SessionId from JWT/headers
      writes all three into Activity baggage
      → HTTP Handler
          → LoggingBehavior (MediatR pipeline)
              reads from Activity baggage → opens logger.BeginScope()
              all log lines in this handler carry the three IDs
              → ServiceBusEventPublisher
                  stamps all three as ApplicationProperties on outgoing message
                  → [Service Bus Topic]
                      → Receiving Processor (another service)
                          reads from ApplicationProperties
                          restores into Activity baggage + logger.BeginScope()
                          → MediatR handler (same IDs now in every log line)
```

### Where It's Implemented

- **`NextAurora.ServiceDefaults/Middleware/CorrelationIdMiddleware.cs`** — HTTP entry point; generates or reads the Correlation ID, reads User ID and Session ID from the authenticated identity, writes all three into `Activity.Current` baggage.
- **`{Service}.Application/Behaviors/LoggingBehavior.cs`** (×4) — MediatR pipeline behavior that reads the three IDs from baggage and opens `logger.BeginScope()` so every handler log line carries them automatically.
- **`{Service}.Infrastructure/Messaging/ServiceBusEventPublisher.cs`** (×3) — stamps the three IDs as `ApplicationProperties` on every outgoing Service Bus message.
- **Each service processor** — reads `ApplicationProperties`, restores IDs into `Activity` baggage and a new `logger.BeginScope()` for the message handler.

For the full deep-dive including log search queries and a new-service checklist, see **[docs/context-propagation.md](context-propagation.md)**.

---

## Distributed Tracing

### ActivitySource for Consumer Spans

All Service Bus message processors create consumer spans using a named `ActivitySource`:

```csharp
private static readonly ActivitySource _activitySource = new("NextAurora.Messaging");

using var processorActivity = _activitySource.StartActivity("ServiceBus.ProcessMessage", ActivityKind.Consumer)
    ?? (Activity.Current is null ? new Activity("ServiceBus.ProcessMessage").Start() : null);
```

The `"NextAurora.Messaging"` source is registered in `Extensions.cs`:

```csharp
tracing.AddSource(builder.Environment.ApplicationName)
       .AddSource("Azure.Messaging.ServiceBus")
       .AddSource("NextAurora.Messaging")
```

**Result:** The Aspire dashboard (and any OTLP-compatible backend — Jaeger, Tempo, Azure Monitor) shows a complete trace from the initial HTTP request through every downstream message, with latency for each hop visible.

### Structured Log Scope

Every processor opens a `logger.BeginScope()` with:
- `CorrelationId`, `UserId`, `SessionId`
- `MessageId` — the Service Bus message GUID (unique per message)
- `Subject` — the event type name (e.g. `"PaymentCompletedEvent"`)
- `DeliveryCount` — how many times this message has been attempted

`DeliveryCount > 1` in your log query means the message was retried — useful for identifying retry storms before they fill the Dead Letter Queue.

---

## Dead Letter Queue (DLQ) Alerting

### Retry Lifecycle

1. Handler throws an exception → `AbandonMessageAsync` is called.
2. Service Bus increments `DeliveryCount` and requeues the message after a backoff delay.
3. When `DeliveryCount` exceeds `MaxDeliveryCount`, the broker moves the message to the Dead Letter Queue — a separate sub-queue at `{topic}/{subscription}/$deadletterqueue`.
4. DLQ messages stay there until replayed or discarded.

### The `messages.abandoned` Metric

Every processor increments the `messages.abandoned` counter when abandoning:

```csharp
_messagesAbandoned.Add(1,
    new KeyValuePair<string, object?>("subject", args.Message.Subject),
    new KeyValuePair<string, object?>("service", "OrderService"));
```

The counter is defined in `NovaCraftMetrics` (`"NextAurora"` meter) and appears in the Aspire metrics dashboard. Configure an alert when this counter rises above your threshold to catch DLQ pile-ups before they cause user-visible outages.

---

## PaymentFailedEvent Handling

### The Gap It Fixes

Previously, when payment failed:
- PaymentService published `PaymentFailedEvent` to `payment-events`
- OrderService deserialized every `payment-events` message as `PaymentCompletedEvent` — the wrong type
- Deserialization returned `null`, the guard skipped silently, `CompleteMessageAsync` was called
- **The order stayed in "Placed" status forever. The buyer was never notified.**

### What's Implemented Now

**OrderService** dispatches `payment-events` messages by `Subject`:

```csharp
if (string.Equals(subject, nameof(PaymentCompletedEvent), StringComparison.Ordinal))
    // → PaymentCompletedHandler → order.MarkAsPaid()
else if (string.Equals(subject, nameof(PaymentFailedEvent), StringComparison.Ordinal))
    // → PaymentFailedHandler → order.MarkAsPaymentFailed()
else
    logger.LogWarning("Unrecognised subject '{Subject}' — completing without processing", subject);
```

**OrderService domain** gained a new status and method:
- `OrderStatus.PaymentFailed` — terminal status for orders where payment was rejected
- `Order.MarkAsPaymentFailed()` — enforces the invariant that only `Placed` orders can transition

**NotificationService** subscribes to `payment-events / notify-sub` and sends a "Payment Failed" email to the buyer when `PaymentFailedEvent` arrives.

**`PaymentFailedEvent`** now carries `BuyerId` so NotificationService can resolve the buyer's contact details without a cross-service call to OrderService.

Both handlers are **idempotent** — they check current order status before applying changes, so replaying a `PaymentFailedEvent` from the DLQ is safe.

---

## Event Replay

The `LoggingEventPublisher` decorator persists every outbound event to an `EventLog` table before publishing (two-phase save):

1. Save `EventLogEntry` with `PublishedAt = null`
2. Publish to Service Bus
3. Stamp `PublishedAt` on success

A row with `PublishedAt = null` means a crash occurred between save and publish — the event can be safely replayed.

Admin replay endpoints are exposed on each service:

```
GET  /admin/events          — list unpublished or all events
POST /admin/events/replay/{eventId}   — replay a specific event
```

For the full replay guide including local reproduction steps, see **[docs/event-replay.md](event-replay.md)**.

---

## Event Catalog

All events — producers, subscribers, topic/subscription names, field schemas, and versioning rules — are documented in **[docs/event-catalog.md](event-catalog.md)**.

---

## Idempotency

All event handlers guard against duplicate delivery (retries, replays):

| Handler | Idempotency Check |
|---|---|
| `PaymentCompletedHandler` | Checks `order.Status != OrderStatus.Placed` before calling `MarkAsPaid()` |
| `PaymentFailedHandler` | Checks `order.Status != OrderStatus.Placed` before calling `MarkAsPaymentFailed()` |
| `ProcessPaymentHandler` | Calls `GetByOrderIdAsync` — returns existing payment if already created |
| `CreateShipmentHandler` | Calls `GetByOrderIdAsync` — skips if shipment already exists |

---

## Log Search Reference

All log lines carry structured fields. Use these queries in your log aggregation tool:

```
# All events in a single transaction
CorrelationId = "a1b2c3d4-..."

# All actions by a specific user
UserId = "user_987"

# All retried messages (potential issues)
DeliveryCount > 1

# All DLQ-bound messages (delivery at or near limit)
DeliveryCount >= 5

# Failed payment events specifically
Subject = "PaymentFailedEvent" AND Level = "Error"
```
