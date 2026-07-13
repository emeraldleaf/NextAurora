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

| Identifier | HTTP Header | Wolverine Envelope Header | Logger Scope Key |
|---|---|---|---|
| Correlation ID | `X-Correlation-Id` | `X-Correlation-Id` | `CorrelationId` |
| User ID | `X-User-Id` | `X-User-Id` | `UserId` |
| Session ID | `X-Session-Id` | `X-Session-Id` | `SessionId` |

`CorrelationIdMiddleware` (HTTP entry) stamps all three into `Activity` baggage; `OutgoingContextMiddleware` writes them onto outgoing Wolverine envelope headers (RabbitMQ message headers on the wire); `ContextPropagationMiddleware` restores them from the envelope headers into `Activity` baggage and a `logger.BeginScope()` on the consuming side. See **[docs/context-propagation.md](context-propagation.md)** for the full developer guide (per-component breakdown, new-service checklist, pitfalls) and **[docs/observability.md](observability.md)** for the technical reference and code patterns.

---

## Distributed Tracing

Saga message spans (send/receive/handle) come from Wolverine's own `ActivitySource("Wolverine")`, registered in `Extensions.cs` — transport-agnostic, so the full event chain is visible in the Aspire dashboard and any OTLP backend regardless of broker (RabbitMQ today). Combined with the `logger.BeginScope()` that `ContextPropagationMiddleware` opens (carrying `CorrelationId`, `UserId`, `SessionId`), every handler log line carries full context automatically. See **[docs/observability.md](observability.md)** for the full OTel configuration, registered sources, and trace span diagram.

---

## Dead Letter Queue (DLQ) Alerting

When a message handler throws, Wolverine applies its retry/error policy; a message that exhausts its retries is dead-lettered by Wolverine's RabbitMQ transport to a Wolverine-managed dead-letter queue on the broker. Dead-lettered messages are visible in the RabbitMQ management UI (`:15672`) and in Wolverine's message store (the `wolverine` schema in each service's database). See **[docs/observability.md#dead-letter-queue-dlq-handling](observability.md)** for the topology table and investigation steps.

### The DLQ alarm signal

Wolverine's own meter is registered in `ServiceDefaults` (`AddMeter("Wolverine*")`), so **`wolverine-dead-letter-queue`** is the metric to alert on — it rises as messages are dead-lettered. (A hand-rolled abandoned-message counter used to be documented here, but nothing incremented it once the old processors were deleted; it was removed in favour of Wolverine's native instruments.)

---

## PaymentFailedEvent Handling

### The Gap It Fixes

Previously, when payment failed:
- PaymentService published `PaymentFailedEvent` to `payment-events`
- OrderService deserialized every `payment-events` message as `PaymentCompletedEvent` — the wrong type
- Deserialization returned `null`, the guard skipped silently, `CompleteMessageAsync` was called
- **The order stayed in "Placed" status forever. The buyer was never notified.**

### What's Implemented Now

**OrderService** handles both event types via Wolverine's type-based dispatch — each event has its own handler class in `Features/`:

- `PaymentCompletedEvent` → `PaymentCompletedHandler` → `order.MarkAsPaid()`
- `PaymentFailedEvent` → `PaymentFailedHandler` → `order.MarkAsPaymentFailed()`

**OrderService domain** gained a new status and method:
- `OrderStatus.PaymentFailed` — terminal status for orders where payment was rejected
- `Order.MarkAsPaymentFailed()` — enforces the invariant that only `Placed` orders can transition

**NotificationService** consumes `payment-events` via its `notify-payments` queue and sends a "Payment Failed" email to the buyer when `PaymentFailedEvent` arrives.

**`PaymentFailedEvent`** now carries `BuyerId` so NotificationService can resolve the buyer's contact details without a cross-service call to OrderService.

Both handlers are **idempotent** — they check current order status before applying changes, so replaying a `PaymentFailedEvent` from the DLQ is safe.

---

## Event Replay

Replay is handled through Wolverine's transactional outbox infrastructure (the `wolverine` schema in each service's database) and its `IMessageStore` API. The previous hand-rolled `EventLog` table and `/admin/events/...` endpoints were deleted as dead code post-Wolverine — see [docs/event-replay.md](event-replay.md) for the short summary and [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) for the full rationale.

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
