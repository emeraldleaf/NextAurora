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

Every request, message, and log line carries three identifiers that link the entire transaction chain:

| Identifier | HTTP Header | Service Bus Property | Logger Scope Key |
|---|---|---|---|
| Correlation ID | `X-Correlation-Id` | `X-Correlation-Id` | `CorrelationId` |
| User ID | `X-User-Id` | `X-User-Id` | `UserId` |
| Session ID | `X-Session-Id` | `X-Session-Id` | `SessionId` |

`CorrelationIdMiddleware` → `ContextPropagationMiddleware` → `WolverineEventPublisher` → each handler restores all three from `ApplicationProperties` into `Activity` baggage and a `logger.BeginScope()`. See **[docs/context-propagation.md](context-propagation.md)** for the full developer guide (per-component breakdown, new-service checklist, pitfalls) and **[docs/observability.md](observability.md)** for the technical reference and code patterns.

---

## Distributed Tracing

All Service Bus processors create consumer spans via `ActivitySource("NextAurora.Messaging")`, registered in `Extensions.cs` alongside `"Azure.Messaging.ServiceBus"`. Combined with the `logger.BeginScope()` that every processor opens (carrying `CorrelationId`, `UserId`, `SessionId`, `MessageId`, `Subject`, and `DeliveryCount`), every handler log line carries full context automatically. `DeliveryCount > 1` signals a retry. See **[docs/observability.md](observability.md)** for the full OTel configuration, registered sources, and trace span diagram.

---

## Dead Letter Queue (DLQ) Alerting

When a message handler throws, the processor calls `AbandonMessageAsync`, incrementing the message's `DeliveryCount`. Once that exceeds `MaxDeliveryCount`, Service Bus moves the message to the Dead Letter Queue. See **[docs/observability.md#dead-letter-queue-dlq-handling](observability.md)** for the full DLQ path table, investigation steps, and transport error logging.

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

The `EventLog` table in each service (Order, Payment, Shipping) provides an admin audit trail and replay capability. Wolverine's EF Core outbox handles delivery guarantees separately.

> **Current state:** `WolverineEventPublisher` does not auto-populate `EventLog` on publish — explicit writes are planned as a follow-up. Admin query/replay endpoints remain functional for any rows present.

Admin replay endpoints are exposed on each service:

```
GET  /admin/events                     — list events (filter by correlationId, entityId, etc.)
POST /admin/events/replay/{eventId}    — replay a specific event
```

For the full replay guide, see **[docs/event-replay.md](event-replay.md)**.

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

See **[docs/context-propagation.md#searching-logs-in-practice](context-propagation.md#searching-logs-in-practice)** for the full log query reference — searching by `CorrelationId`, `UserId`, `SessionId`, `DeliveryCount`, `Subject`, and more.
