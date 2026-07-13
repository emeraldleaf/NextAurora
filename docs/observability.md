# Observability & Debuggability Guide

This document describes the observability features added to NextAurora, how they work together, and how to use them when debugging production issues.

---

## Overview

Every request or event in NextAurora now carries a **Correlation ID** that flows from the initial HTTP call through every RabbitMQ message and log line across all services. Combined with OpenTelemetry distributed tracing, structured logging, Wolverine pipeline telemetry, business metrics, and Dead Letter Queue (DLQ) handling, this gives you a complete picture of any transaction — even when it spans five microservices.

---

## Correlation ID Propagation

### How It Works

`CorrelationIdMiddleware` (registered globally in `ServiceDefaults`) runs on every HTTP request:

1. Reads the `X-Correlation-Id` request header. If absent, generates one from the active W3C trace ID or a new GUID.
2. Stores it in `Activity` baggage (`correlation.id`) so it is automatically carried through any downstream HTTP or gRPC calls via W3C TraceContext propagation.
3. Opens an `ILogger` scope enriched with `CorrelationId`, so every log line written during that request automatically includes the value.
4. Echoes the ID in the `X-Correlation-Id` response header so clients can record it.

### Propagation Through RabbitMQ

When a service publishes an event, `OutgoingContextMiddleware` (Wolverine outgoing-envelope middleware) reads the IDs from `Activity` baggage and stamps them onto the Wolverine envelope, which the RabbitMQ transport carries as message headers:

```csharp
envelope.Headers["X-Correlation-Id"] = correlationId;
envelope.Headers["X-User-Id"]        = userId;      // only when present
envelope.Headers["X-Session-Id"]     = sessionId;   // only when present
```

When a message arrives, `ContextPropagationMiddleware` (Wolverine incoming middleware) reads the envelope headers back into `Activity` baggage and opens a logging scope before the handler runs:

```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["CorrelationId"] = correlationId,
    ["UserId"]        = userId,      // added only when present
    ["SessionId"]     = sessionId    // added only when present
});
```

Every log line written by the handler (and anything it calls transitively) will carry those fields.

### Finding a Transaction

Given a correlation ID (from a client error report or response header), you can retrieve the entire cross-service trace in any structured log sink:

```
CorrelationId = "a3f1b2c4..."
```

This returns every log line — HTTP request, Wolverine handler, RabbitMQ publish, RabbitMQ receive, and notification send — for that single transaction.

For the full three-identifier guide (UserId, SessionId, new-service checklist, common pitfalls), see **[docs/context-propagation.md](context-propagation.md)**.

---

## Distributed Tracing (OpenTelemetry)

### What Is Traced

`ServiceDefaults` configures OpenTelemetry tracing with the following sources:

| Source | What It Covers |
|--------|----------------|
| `{ServiceName}` (application name) | Custom spans per service |
| `Wolverine` | Message send/receive/handle spans for the saga — transport-agnostic (RabbitMQ today) |
| `NextAurora.Messaging` | Registered but currently dormant — no code emits spans under this name today; saga message spans come from the `Wolverine` source |
| ASP.NET Core instrumentation | Inbound HTTP requests |
| gRPC client instrumentation | OrderService → CatalogService gRPC calls |
| HTTP client instrumentation | All outbound HTTP calls |

Health check endpoints (`/health`, `/alive`) are excluded from traces to reduce noise.

### Viewing Traces

When `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, all traces are exported via OTLP. In local development with Aspire, traces are visible in the Aspire dashboard. In production, connect any OTLP-compatible backend (Jaeger, Tempo, Azure Monitor, etc.).

A single trace for an order placement will show spans across:

```
[OrderService] POST /orders
  └─ [OrderService] PlaceOrderCommand handler
       └─ [CatalogService gRPC] GetProduct / ReserveStock
       └─ [Wolverine] Send → order-events
            ├─ [NotificationService] OrderPlaced handler
            └─ [PaymentService] OrderPlaced handler
                 └─ [PaymentService] ProcessPayment handler
                      └─ [Wolverine] Send → payment-events
                           ├─ [ShippingService] PaymentCompleted handler
                           └─ [NotificationService] PaymentCompleted handler
```

---

## Wolverine Pipeline Logging

Every command and query in Order, Payment, Catalog, and Shipping services passes through the Wolverine middleware pipeline. Two components handle observability:

- **`Policies.LogMessageStarting(LogLevel.Information)`** — Wolverine built-in; logs handler name and elapsed time automatically.
- **`ContextPropagationMiddleware`** (in `ServiceDefaults`) — runs before every handler; reads the three IDs from `Activity.Current` baggage and opens a `logger.BeginScope()` for the duration of the handler.

For each handler execution it logs:

- **Start**: handler name + elapsed (Wolverine built-in)
- **Scope**: every log line inside the handler automatically carries `CorrelationId`, `UserId`, `SessionId`

Example log output:

```
[INF] Handling PlaceOrderCommand (CorrelationId: a3f1b2c4...)
[INF] Handled PlaceOrderCommand in 142ms
```

On failure:

```
[INF] Handling PlaceOrderCommand (CorrelationId: a3f1b2c4...)
[WRN] Failed PlaceOrderCommand after 38ms
```

The exception itself is handled and logged by `GlobalExceptionHandler` in `ServiceDefaults`, which formats it as a `ProblemDetails` response including the trace ID.

---

## Dead Letter Queue (DLQ) Handling

When a message handler throws, Wolverine applies the configured error policy (e.g. the `AddConcurrencyRetry` cooldown retries for `DbUpdateConcurrencyException`). A message that exhausts its retries is dead-lettered by Wolverine's RabbitMQ transport to a Wolverine-managed dead-letter queue on the broker.

### The RabbitMQ Topology

Each event family has a fanout exchange with one queue per consumer bound to it:

| Fanout Exchange | Consumer Queues |
|-----------------|-----------------|
| `order-events` | `payment-orders`, `notify-orders` |
| `payment-events` | `order-payments`, `shipping-payments`, `notify-payments` |
| `shipping-events` | `order-shipping`, `notify-shipping` |
| — (direct send) | `send-notification` |

### Investigating a Dead-Lettered Message

1. Open the RabbitMQ management UI (`http://localhost:15672` in local dev) and inspect the dead-letter queue, or query Wolverine's message store (the `wolverine` schema in each service's database).
2. Check the `X-Correlation-Id` message header to retrieve the original correlation ID.
3. Search your log sink with that ID to see the full history of attempts.
4. Fix the root cause, then replay via Wolverine's `IMessageStore` / DLQ tooling (see [Event Replay](#event-replay) below).

---

## Business Metrics

A `Meter("NextAurora")` is registered in `ServiceDefaults` and collected by the OpenTelemetry metrics pipeline. The following counters are incremented by the relevant handlers:

| Metric Name | Incremented By | Tags |
|-------------|----------------|------|
| `orders.placed` | `PlaceOrderHandler` | — |
| `payments.processed` | `ProcessPaymentHandler` | `outcome=success\|failed` |
| `shipments.dispatched` | `CreateShipmentHandler` | — |
| `notifications.sent` | `SendNotificationHandler` | `channel=Email\|…` |
| `messages.abandoned` | Nothing currently — declared in `NextAuroraMetrics`, but the processors that incremented it were deleted in the RabbitMQ/Wolverine migration; re-wiring it is a tracked follow-up. Monitor DLQ depth via the RabbitMQ management UI (`:15672`) or the `wolverine` schema instead | `subject=<EventType>`, `service=<ServiceName>` |

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
| `NextAurora.ServiceDefaults/Metrics/NextAuroraMetrics.cs` | Business metrics counters |
| `NextAurora.ServiceDefaults/Messaging/ContextPropagationMiddleware.cs` | Wolverine middleware — opens `BeginScope` with CorrelationId/UserId/SessionId for every handler |

### Modified Files

| File | Change |
|------|--------|
| `NextAurora.ServiceDefaults/Extensions.cs` | Register middleware; add `Wolverine` trace source + NextAurora meter; enable health checks in all environments |
| `Directory.Packages.props` | Added `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.2` |
| `OutgoingContextMiddleware` (Wolverine) + handler publishing via the enlisted `IMessageContext` / `IDbContextOutbox` | Context propagation on outgoing messages; Wolverine EF Core outbox for delivery guarantees (publish enlisted in the entity transaction — see the Wolverine 5→6 upgrade notes) |
| `{Order,Payment,Shipping,Notification}Service` (Wolverine handlers) | Context extraction + structured logging scope via `ContextPropagationMiddleware`; failed messages dead-lettered by Wolverine's retry/error policy |
| `{Order,Payment,Catalog,Shipping}Service.Infrastructure/DependencyInjection.cs` | Added `AddDbContextCheck<T>()` |
| `{Order,Payment,Catalog,Shipping}Service.Infrastructure/*.csproj` | Added EF Core health checks package reference |
| `{Payment,Catalog,Shipping}Service.Application/*.csproj` | Added `Microsoft.Extensions.Logging.Abstractions` |
| `{Order,Payment,Catalog,Shipping}Service.Api/Program.cs` | Registered `ContextPropagationMiddleware` + `Policies.LogMessageStarting()` in Wolverine pipeline |
| `OrderService/Features/PlaceOrder.cs` | Increments `orders.placed` counter |
| `PaymentService/Features/ProcessPayment.cs` | Increments `payments.processed` counter with outcome tag |
| `ShippingService/Features/CreateShipment.cs` | Increments `shipments.dispatched` counter |
| `NotificationService/Features/SendNotification.cs` | Increments `notifications.sent` counter with channel tag |

---

## Event Replay

Replay is handled through Wolverine's transactional outbox (`wolverine` schema in each service's database) and its `IMessageStore` API. The previous hand-rolled `EventLog` table and `/admin/events/...` endpoints were deleted as dead code post-Wolverine. See [docs/event-replay.md](event-replay.md) for context.
