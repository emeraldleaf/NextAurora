# Event Replay — Developer Guide
---

## Table of Contents

1. [Background — What is an Event?](#1-background--what-is-an-event)
2. [The Problem Event Replay Solves](#2-the-problem-event-replay-solves)
3. [How the System Works](#3-how-the-system-works)
4. [The EventLogEntry Table](#4-the-eventlogentry-table)
5. [Using the Admin Endpoints](#5-using-the-admin-endpoints)
6. [Worked Example — Debugging a Failed Shipment](#6-worked-example--debugging-a-failed-shipment)
7. [How Replay Messages Are Marked](#7-how-replay-messages-are-marked)
8. [Configuration and Setup](#8-configuration-and-setup)
9. [Code Architecture — The Decorator Pattern](#9-code-architecture--the-decorator-pattern)
10. [Common Questions](#10-common-questions)

---

## 1. Background — What is an Event?

In NextAurora, services communicate by publishing **events** — small JSON messages sent over Azure Service Bus. When a customer places an order, for example, the order service sends an `OrderPlacedEvent` message. The payment service reads that message and starts processing the payment. The shipping service reads the payment success message and creates a shipment.

```
Customer         OrderService           PaymentService         ShippingService
    |                 |                       |                      |
    |-- POST /orders ->|                       |                      |
    |                 |-- OrderPlacedEvent --> |                      |
    |                 |                       |-- PaymentCompletedEvent --> |
    |                 |                       |                      |-- ShipmentDispatchedEvent
```

Each arrow is a message on a Service Bus topic. These messages are **asynchronous** — the order service does not wait for the payment service to respond. It just fires the event and moves on.

This design is great for scalability, but it creates a debugging challenge: **if something goes wrong mid-chain, how do you know what happened and where?**

---

## 2. The Problem Event Replay Solves

Imagine a customer reports "my order was placed but never shipped." You open the logs and see the `OrderPlacedEvent` was published. But did the payment service receive it? Did the shipping service get triggered?

Without event replay, you would:
1. Search logs across three services manually
2. Hope the logs haven't been rotated/purged
3. Not be able to reproduce the issue

**With event replay, you can:**
1. Query the event log for that order's correlation ID — see every event that was published
2. See exactly which events succeeded (have a `PublishedAt` timestamp) and which failed (null `PublishedAt`)
3. Re-trigger the missing event without restarting services or re-creating test data

---

## 3. How the System Works

### The Publishing Path

When a handler publishes an event, `WolverineEventPublisher` delegates to `IMessageBus.PublishAsync()`. Wolverine's EF Core outbox (`WolverineFx.EntityFrameworkCore`) guarantees at-least-once delivery by persisting the outgoing envelope in the same database transaction as the domain change.

```
Handler calls IEventPublisher.PublishAsync(event)
         │
         ▼
┌─────────────────────────────────┐
│      WolverineEventPublisher    │
│                                 │
│  bus.PublishAsync(@event)       │  ← queued in Wolverine EF Core outbox
│                                 │
└─────────────────────────────────┘
         │
         ▼  (Wolverine background sender picks up from outbox)
   Message arrives at consuming service
```

### The EventLog Table (Audit / Replay)

The `EventLogs` table exists in Order, Payment, and Shipping services for **admin audit and replay** purposes. It is separate from Wolverine's delivery outbox. Currently, entries must be written explicitly — `WolverineEventPublisher` does not auto-populate `EventLog` on every publish. The admin `/admin/events` endpoints query and replay from this table.

> **Note:** Automatically populating EventLog on every publish (as `LoggingEventPublisher` previously did) is planned as a follow-up. For now, EventLog rows are written only when explicitly created (e.g. by admin tooling or future interceptor middleware).

---

## 4. The EventLogEntry Table

Each of the three event-publishing services (Order, Payment, Shipping) has an `EventLogs` table in its own database.

| Column | What it Contains | Example |
|--------|-----------------|---------|
| `Id` | Unique ID for this log entry | `a1b2c3d4-...` |
| `EventType` | Name of the event class | `OrderPlacedEvent` |
| `Topic` | Service Bus topic where it was sent | `order-events` |
| `Payload` | The full JSON body of the message | `{"OrderId":"...","BuyerId":"..."}` |
| `CorrelationId` | The trace ID linking this to the original HTTP request | `abc-123-xyz` |
| `EntityId` | The aggregate root ID extracted from the payload | `<orderId guid>` |
| `OccurredAt` | When the publisher was called | `2024-03-06T10:22:00Z` |
| `PublishedAt` | When Service Bus confirmed receipt. **Null = failed** | `2024-03-06T10:22:01Z` |
| `IsReplay` | Was this row created by a replay request? | `false` |
| `OriginalEventId` | For replays — which original event was this based on? | `null` (or a Guid) |

> **Key insight:** If `PublishedAt` is `null`, the message was never delivered. If it has a value, the message reached Service Bus (though it may still have failed processing on the consumer side — that's a separate concern).

---

## 5. Using the Admin Endpoints

All admin endpoints are protected by an `X-Admin-Key` header. You will need this key from your environment's configuration (ask a senior team member for the value — it should never be stored in source control).

All services expose the same three endpoints at `/admin/events`.

### 5.1 Query the Event Log

**Find all events for a specific order:**
```bash
curl -H "X-Admin-Key: your-secret-key" \
  "https://order-service/admin/events?entityId=<orderId>"
```

**Find all events in a correlation chain (all services involved in one request):**
```bash
# Run this against each service to get the full picture
curl -H "X-Admin-Key: your-secret-key" \
  "https://order-service/admin/events?correlationId=abc-123-xyz"

curl -H "X-Admin-Key: your-secret-key" \
  "https://payment-service/admin/events?correlationId=abc-123-xyz"

curl -H "X-Admin-Key: your-secret-key" \
  "https://shipping-service/admin/events?correlationId=abc-123-xyz"
```

**Find all failed publishes in the last hour:**
```bash
curl -H "X-Admin-Key: your-secret-key" \
  "https://order-service/admin/events?from=2024-03-06T09:00:00Z&to=2024-03-06T10:00:00Z"
```

**Filter parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `correlationId` | string | Trace ID from `X-Correlation-Id` header |
| `eventType` | string | Exact event class name, e.g. `OrderPlacedEvent` |
| `entityId` | string | The order/payment/shipment ID |
| `from` | ISO 8601 | `OccurredAt >=` |
| `to` | ISO 8601 | `OccurredAt <=` |
| `page` | int | Page number (default 1) |
| `pageSize` | int | Results per page (max 100, default 50) |

**Example response:**
```json
{
  "total": 2,
  "page": 1,
  "pageSize": 50,
  "items": [
    {
      "id": "a1b2c3d4-0000-0000-0000-000000000001",
      "eventType": "OrderPlacedEvent",
      "topic": "order-events",
      "correlationId": "abc-123-xyz",
      "entityId": "550e8400-e29b-41d4-a716-446655440000",
      "occurredAt": "2024-03-06T10:22:00Z",
      "publishedAt": "2024-03-06T10:22:01Z",  ← successful
      "isReplay": false,
      "originalEventId": null
    },
    {
      "id": "a1b2c3d4-0000-0000-0000-000000000002",
      "eventType": "OrderPlacedEvent",
      "topic": "order-events",
      "correlationId": "abc-123-xyz",
      "entityId": "550e8400-e29b-41d4-a716-446655440000",
      "occurredAt": "2024-03-06T10:22:05Z",
      "publishedAt": null,  ← FAILED — Service Bus publish failed
      "isReplay": false,
      "originalEventId": null
    }
  ]
}
```

### 5.2 Replay a Single Event

Takes the `id` of any event log entry and re-publishes its payload to the original Service Bus topic.

```bash
curl -X POST \
  -H "X-Admin-Key: your-secret-key" \
  "https://order-service/admin/events/a1b2c3d4-0000-0000-0000-000000000002/replay"
```

**Response:**
```json
HTTP/1.1 202 Accepted
{
  "replayEventLogId": "b2c3d4e5-..."
}
```

The `replayEventLogId` is the ID of the new `EventLogEntry` row created for this replay. You can use it to confirm the replay was published successfully (check that its `PublishedAt` is not null).

### 5.3 Replay a Full Transaction Chain

Replays **all original (non-replay) events** for a given correlation ID, in the order they originally occurred. Use this when you want to re-drive an entire transaction from a specific service.

```bash
curl -X POST \
  -H "X-Admin-Key: your-secret-key" \
  "https://order-service/admin/events/replay-chain?correlationId=abc-123-xyz"
```

**Response:**
```json
HTTP/1.1 202 Accepted
{
  "replayedCount": 1
}
```

> ⚠️ **Be careful with replay-chain on high-traffic IDs.** It replays every non-replay event for that correlation ID. If a correlation ID covers multiple orders (shouldn't happen normally but check first), you'd replay all of them.

---

## 6. Worked Example — Debugging a Failed Shipment

Let's walk through a real scenario: a customer placed an order, the payment went through, but no shipment was created.

**Step 1: Get the Correlation ID**

The customer provides their order ID: `550e8400-e29b-41d4-a716-446655440000`.

First, find the correlation ID for this order's placement. Check the order service event log:

```bash
curl -H "X-Admin-Key: $ADMIN_KEY" \
  "https://order-service/admin/events?entityId=550e8400-e29b-41d4-a716-446655440000"
```

You see one `OrderPlacedEvent` with `correlationId: "abc-123-xyz"` and a valid `publishedAt`. Good — the order event was published successfully.

**Step 2: Check the Payment Service**

```bash
curl -H "X-Admin-Key: $ADMIN_KEY" \
  "https://payment-service/admin/events?correlationId=abc-123-xyz"
```

You see one `PaymentCompletedEvent` with `publishedAt: null`. **Found it** — the payment service processed the payment but the Service Bus publish failed. The shipping service never received the trigger.

**Step 3: Replay the Failed Event**

Copy the `id` of the failed entry (say `c3d4e5f6-...`) and replay it:

```bash
curl -X POST \
  -H "X-Admin-Key: $ADMIN_KEY" \
  "https://payment-service/admin/events/c3d4e5f6-.../replay"
```

The shipping service will now receive the `PaymentCompletedEvent` and create the shipment.

**Step 4: Verify**

```bash
curl -H "X-Admin-Key: $ADMIN_KEY" \
  "https://shipping-service/admin/events?correlationId=abc-123-xyz"
```

You should now see a `ShipmentDispatchedEvent` entry. Done!

---

## 7. How Replay Messages Are Marked

Replayed messages carry two extra Service Bus application properties:

| Property | Value | Purpose |
|----------|-------|---------|
| `X-Replay` | `"true"` | Flags the message as a replay |
| `X-Replay-Of` | original event log ID | Traceability — which original event triggered this |

Consumers currently process replays identically to original messages (this is correct — all event handlers are designed to be **idempotent**: running the same handler twice produces the same result, not doubled data). If you ever need to skip certain logic on replays, you can check these properties in the processor:

```csharp
// In a ServiceBusProcessor handler (example only — not current code)
var isReplay = message.ApplicationProperties.TryGetValue("X-Replay", out var val) && val?.ToString() == "true";
```

---

## 8. Configuration and Setup

### Setting the Admin Key

Add to each service's `appsettings.Development.json` (never commit this value):

```json
{
  "AdminApiKey": "dev-only-change-me-in-prod"
}
```

In production, set it as an environment variable or via Azure Key Vault:
```
AdminApiKey=<cryptographically-random-value>
```

If `AdminApiKey` is not set, all `/admin/events` requests will return `403 Forbidden` (fail-closed design — better to lock everyone out than to have an unprotected endpoint).

### Running Database Migrations

The `EventLogs` table needs to be added to each service's database. After pulling the latest code, run:

```bash
# Install EF tools globally (one-time setup)
dotnet tool install --global dotnet-ef

# OrderService (SQL Server)
dotnet ef migrations add AddEventLog \
  --project OrderService/OrderService.Infrastructure \
  --startup-project OrderService/OrderService.Api

dotnet ef database update \
  --project OrderService/OrderService.Infrastructure \
  --startup-project OrderService/OrderService.Api

# PaymentService (SQL Server)
dotnet ef migrations add AddEventLog \
  --project PaymentService/PaymentService.Infrastructure \
  --startup-project PaymentService/PaymentService.Api

dotnet ef database update \
  --project PaymentService/PaymentService.Infrastructure \
  --startup-project PaymentService/PaymentService.Api

# ShippingService (PostgreSQL)
dotnet ef migrations add AddEventLog \
  --project ShippingService/ShippingService.Infrastructure \
  --startup-project ShippingService/ShippingService.Api

dotnet ef database update \
  --project ShippingService/ShippingService.Infrastructure \
  --startup-project ShippingService/ShippingService.Api
```

> Run `dotnet aspire run` first to make sure the databases are up, then in a separate terminal run the `database update` commands.

---

## 9. Code Architecture — WolverineEventPublisher

Each event-publishing service has a `WolverineEventPublisher` that bridges `IEventPublisher` (domain interface) to Wolverine's `IMessageBus`:

```
IEventPublisher  (interface — Domain layer, no dependencies)
      │
      └── WolverineEventPublisher  ← bridges to IMessageBus.PublishAsync()
                                      topic routing configured in UseWolverine()
                                      in Program.cs — not at the call site
```

This is how DI is wired in `DependencyInjection.cs`:

```csharp
services.AddScoped<IEventPublisher, WolverineEventPublisher>();
```

Delivery guarantees are provided by **Wolverine's EF Core outbox** (`WolverineFx.EntityFrameworkCore`) — it persists outgoing envelopes in the same database transaction as the aggregate change, then a background sender delivers them. This replaces the old three-phase `LoggingEventPublisher` pattern.

The `EventLog` table remains for admin audit/replay queries. Unlike the old decorator, writes to `EventLog` are not automatic — they must be wired up explicitly if event-level audit logging is required.

---

## 10. Common Questions

**Q: Does replay send the event twice if I call it on a successfully-published event?**

A: Yes, it does. The replay endpoint does not check whether the original event was successfully published — it just re-sends. Always check `publishedAt` before replaying. If `publishedAt` has a value, the original was delivered and you should think carefully before replaying (idempotent handlers are safe, but extra events still add noise).

---

**Q: Why is `PublishedAt` sometimes null even though the order was processed correctly?**

A: The event log save and the domain data save are in separate database transactions. There's a tiny window where:
1. The domain save succeeds (order is persisted)
2. The event log save succeeds (row created with `PublishedAt = null`)
3. Service Bus publish succeeds
4. The second save to set `PublishedAt` fails (transient DB hiccup)

In this case the event was delivered, but the log shows `PublishedAt = null`. Always cross-check with the consuming service's own event log before replaying.

---

**Q: Does event replay work for the Notification Service?**

A: No. The Notification Service has no database (it only reads from Service Bus queues and sends notifications). There is no event log for it. If a notification was missed, trace back to whichever service published the triggering event (e.g., ShippingService for shipment notifications) and replay from there.

---

**Q: Can I replay events in a different order or modify the payload?**

A: Not via these endpoints — they re-publish the stored payload exactly as-is. This is intentional: replaying modified data could create consistency problems. If you need to replay with different data, that's a manual data-fix process — speak to a senior engineer.

---

**Q: What happens to old event log entries?**

A: Nothing automatically — they accumulate in the database. There is currently no retention/purge policy. This is a known gap; a background cleanup job or table partition strategy will be added when storage becomes a concern.

---

**Q: How do I find the Correlation ID if I only have a user's email or order number?**

A: Query the service's main database (e.g., the orders table) to find the Order ID, then use `?entityId=<orderId>` to get the event log entries and read the `correlationId` from there.

---

*For the broader observability setup (structured logging, distributed tracing, DLQ handling), see [`observability.md`](./observability.md).*
