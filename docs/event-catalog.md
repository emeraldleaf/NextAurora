# Event Catalog

This catalog documents every domain event in NextAurora: who publishes it, who consumes it, and what fields it carries. Keep this document up to date when adding or changing events.

---

## Exchange / Queue Matrix (RabbitMQ)

One **fanout exchange** per event family; one queue per consumer bound to it (naming: `{consumer}-{source-events}`). **Each publisher declares its own exchange AND its consumers' queues+bindings** (consumers keep their own bindings too — idempotent declares, no boot-order gap), AutoProvisioned by Wolverine at startup. The code-side source of truth for these names is [`NextAurora.Contracts/Messaging/MessagingTopology.cs`](../NextAurora.Contracts/Messaging/MessagingTopology.cs) — **this matrix and that file update together**.

| Exchange | Publisher | Bound queue | Consumer |
|---|---|---|---|
| `order-events` | OrderService | `payment-orders` | PaymentService |
| `order-events` | OrderService | `notify-orders` | NotificationService |
| `payment-events` | PaymentService | `order-payments` | OrderService |
| `payment-events` | PaymentService | `shipping-payments` | ShippingService |
| `payment-events` | PaymentService | `notify-payments` | NotificationService |
| `shipping-events` | ShippingService | `order-shipping` | OrderService |
| `shipping-events` | ShippingService | `notify-shipping` | NotificationService |

> `notify-payments` is bound to the whole `payment-events` exchange, but NotificationService only has a handler for `PaymentFailedEvent` — `PaymentCompletedEvent` arrives on that queue and is ignored.

---

## Events

### `OrderPlacedEvent`

**Exchange:** `order-events`  
**Subject header:** `OrderPlacedEvent`  
**Producer:** OrderService (`PlaceOrderHandler`)  
**Consumers:** PaymentService → triggers payment processing; NotificationService → sends "Order Received" email

| Field | Type | Description |
|---|---|---|
| `OrderId` | `Guid` | Unique order identifier |
| `BuyerId` | `Guid` | User who placed the order |
| `PlacedAt` | `DateTime` | UTC timestamp |
| `TotalAmount` | `decimal` | Sum of all line items |
| `Currency` | `string` | ISO 4217 currency code (e.g. `"USD"`) |
| `Lines` | `List<OrderLineContract>` | Line items (ProductId, ProductName, Quantity, UnitPrice) |

---

### `PaymentCompletedEvent`

**Exchange:** `payment-events`  
**Subject header:** `PaymentCompletedEvent`  
**Producer:** PaymentService (`PaymentProcessingRequestedHandler`, after the gateway call; `ProcessPaymentHandler` re-publishes only on the idempotent retry of an already-completed payment)  
**Consumers:** OrderService → marks order as `Paid`; ShippingService → creates shipment

| Field | Type | Description |
|---|---|---|
| `PaymentId` | `Guid` | Payment record identifier |
| `OrderId` | `Guid` | Associated order |
| `BuyerId` | `Guid` | Buyer who placed the order (set from `payment.BuyerId`) |
| `Amount` | `decimal` | Amount charged |
| `Provider` | `string` | Payment gateway name (e.g. `"Stripe"`) |
| `CompletedAt` | `DateTime` | UTC timestamp of successful charge |

---

### `PaymentFailedEvent`

**Exchange:** `payment-events`  
**Subject header:** `PaymentFailedEvent`  
**Producer:** PaymentService (`PaymentProcessingRequestedHandler` on gateway failure; `ProcessPaymentHandler` on the idempotent retry of an already-failed payment; `PaymentRecoveryJob` for stuck `Pending` payments, skipped for legacy rows with no `BuyerId`)  
**Consumers:** OrderService → marks order as `PaymentFailed`; NotificationService → sends "Payment Failed" email

| Field | Type | Description |
|---|---|---|
| `PaymentId` | `Guid` | Payment record identifier |
| `OrderId` | `Guid` | Associated order |
| `BuyerId` | `Guid` | Buyer, included so NotificationService can look up contact details without calling OrderService |
| `Reason` | `string` | Human-readable failure reason from the gateway (e.g. `"Card declined"`) |
| `FailedAt` | `DateTime` | UTC timestamp |

---

### `ShipmentDispatchedEvent`

**Exchange:** `shipping-events`  
**Subject header:** `ShipmentDispatchedEvent`  
**Producer:** ShippingService (`CreateShipmentHandler`)  
**Consumers:** OrderService → marks order as `Shipped`; NotificationService → sends "Order Shipped" email

| Field | Type | Description |
|---|---|---|
| `ShipmentId` | `Guid` | Shipment record identifier |
| `OrderId` | `Guid` | Associated order |
| `TrackingNumber` | `string` | Carrier tracking reference |
| `Carrier` | `string` | Shipping carrier name |
| `DispatchedAt` | `DateTime` | UTC timestamp |

---


---

## Observability Headers

Messages can carry these headers on the RabbitMQ envelope (a header is absent when its value is unknown — system events, for example, have no user):

| Property | Description |
|---|---|
| `X-Correlation-Id` | Chain ID linking all events in a single user transaction (optional; honoured on receive) |
| `X-User-Id` | Authenticated user who initiated the chain (null for system events) |
| `X-Session-Id` | Browser/app session ID (null for system events) |

`X-User-Id` and `X-Session-Id` are stamped by `OutgoingContextMiddleware` (in `ServiceDefaults`) when the values are present in Activity baggage. `X-Correlation-Id` is not stamped on outgoing envelopes: correlation travels via Wolverine's built-in envelope `CorrelationId` (from the W3C traceparent). The receiving `ContextPropagationMiddleware` honours an explicit `X-Correlation-Id` header first, then falls back to `envelope.CorrelationId`, then the current trace ID, and restores everything into baggage and the logger scope. See `docs/context-propagation.md` for the full propagation guide.

---

## Versioning Rules

1. **Adding a new field** — safe. All consumers ignore unknown fields during JSON deserialization. Add the field as optional (with a default) in the C# record.
2. **Renaming a field** — breaking change. Coordinate a dual-publish/dual-read migration window, or use a new event type name.
3. **Removing a field** — breaking change for any consumer that depends on it. Check all subscribers in this catalog before removing.
4. **New event type** — add it to this catalog. Add a Wolverine handler in every consumer listed in the exchange/queue matrix.

---

## Dead Letter Queues

Wolverine's RabbitMQ transport dead-letters exhausted messages to a Wolverine-managed dead-letter queue on the broker (visible alongside the consumer queues in the RabbitMQ management UI at `:15672`).

Messages land there after exhausting Wolverine's retry policy. Wolverine's own `wolverine-dead-letter-queue` OTel counter is the alarm signal — its meter is registered in `ServiceDefaults`.

Replay is available through Wolverine's transactional outbox tables (`wolverine` schema in each publishing service's database — Order, Payment, Shipping) and its `IMessageStore` API. NotificationService has no message store; its listeners run `ProcessInline` with no durable inbox, so there is no outbox or replay there. See [docs/event-replay.md](event-replay.md).
