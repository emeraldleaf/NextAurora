# Event Catalog

This catalog documents every domain event in NextAurora: who publishes it, who consumes it, and what fields it carries. Keep this document up to date when adding or changing events.

---

## Exchange / Queue Matrix (RabbitMQ)

One **fanout exchange** per event family; each consumer binds its own queue (naming: `{consumer}-{source-events}`). Wolverine declares and AutoProvisions all of it from each service's `Program.cs`.

| Exchange | Publisher | Bound queue | Consumer |
|---|---|---|---|
| `order-events` | OrderService | `payment-orders` | PaymentService |
| `order-events` | OrderService | `notify-orders` | NotificationService |
| `payment-events` | PaymentService | `order-payments` | OrderService |
| `payment-events` | PaymentService | `shipping-payments` | ShippingService |
| `payment-events` | PaymentService | `notify-payments` | NotificationService |
| `shipping-events` | ShippingService | `order-shipping` | OrderService |
| `shipping-events` | ShippingService | `notify-shipping` | NotificationService |
| `send-notification` *(direct queue, no exchange)* | Any service | `send-notification` | NotificationService |

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
**Producer:** PaymentService (`ProcessPaymentHandler`)  
**Consumers:** OrderService → marks order as `Paid`; ShippingService → creates shipment

| Field | Type | Description |
|---|---|---|
| `PaymentId` | `Guid` | Payment record identifier |
| `OrderId` | `Guid` | Associated order |
| `Amount` | `decimal` | Amount charged |
| `Provider` | `string` | Payment gateway name (e.g. `"Stripe"`) |
| `CompletedAt` | `DateTime` | UTC timestamp of successful charge |

---

### `PaymentFailedEvent`

**Exchange:** `payment-events`  
**Subject header:** `PaymentFailedEvent`  
**Producer:** PaymentService (`ProcessPaymentHandler`)  
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

## Commands (RabbitMQ Queue)

### `SendNotificationCommand`

**Queue:** `send-notification`  
**Producers:** Any service that needs to trigger a notification without knowing the delivery channel  
**Consumer:** NotificationService → dispatches to `SendNotificationHandler`

| Field | Type | Description |
|---|---|---|
| `RecipientId` | `Guid` | Buyer/user identifier |
| `RecipientEmail` | `string` | Resolved email address |
| `Subject` | `string` | Notification subject line |
| `Body` | `string` | Notification body text |
| `Channel` | `string` | Delivery channel: `"Email"`, `"Push"`, `"SMS"` |

---

## Observability Headers

All messages carry these headers on every RabbitMQ message envelope:

| Property | Description |
|---|---|
| `X-Correlation-Id` | Chain ID linking all events in a single user transaction |
| `X-User-Id` | Authenticated user who initiated the chain (null for system events) |
| `X-Session-Id` | Browser/app session ID (null for system events) |

These are stamped by `OutgoingContextMiddleware` (in `ServiceDefaults`) onto every outgoing Wolverine envelope, and restored by `ContextPropagationMiddleware` in each receiving handler. See `docs/context-propagation.md` for the full propagation guide.

---

## Versioning Rules

1. **Adding a new field** — safe. All consumers ignore unknown fields during JSON deserialization. Add the field as optional (with a default) in the C# record.
2. **Renaming a field** — breaking change. Coordinate a dual-publish/dual-read migration window, or use a new event type name.
3. **Removing a field** — breaking change for any consumer that depends on it. Check all subscribers in this catalog before removing.
4. **New event type** — add it to this catalog. Add a Wolverine handler in every consumer listed in the exchange/queue matrix.

---

## Dead Letter Queues

Wolverine's RabbitMQ transport dead-letters exhausted messages to a Wolverine-managed dead-letter queue on the broker (visible alongside the consumer queues in the RabbitMQ management UI at `:15672`).

Messages land there after exhausting Wolverine's retry policy. The `messages.abandoned` OTel counter (tagged with `subject` and `service`) rises as messages approach the DLQ. Alert when this counter crosses your threshold.

Replay is available through Wolverine's transactional outbox tables (`wolverine` schema in each service's database) and its `IMessageStore` API. See [docs/event-replay.md](event-replay.md).
