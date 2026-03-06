# Event Catalog

This catalog documents every domain event in NextAurora: who publishes it, who consumes it, and what fields it carries. Keep this document up to date when adding or changing events.

---

## Topic / Subscription Matrix

| Topic | Publisher | Subscription | Subscriber |
|---|---|---|---|
| `order-events` | OrderService | `payment-sub` | PaymentService |
| `order-events` | OrderService | `shipping-sub` | *(reserved — ShippingService subscribes via PaymentService cascade)* |
| `order-events` | OrderService | `notify-sub` | NotificationService |
| `payment-events` | PaymentService | `order-sub` | OrderService |
| `payment-events` | PaymentService | `shipping-sub` | ShippingService |
| `payment-events` | PaymentService | `notify-sub` | NotificationService |
| `shipping-events` | ShippingService | `order-sub` | OrderService |
| `shipping-events` | ShippingService | `notify-sub` | NotificationService |
| `send-notification` *(queue)* | Any service | *(queue)* | NotificationService |

---

## Events

### `OrderPlacedEvent`

**Topic:** `order-events`  
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

**Topic:** `payment-events`  
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

**Topic:** `payment-events`  
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

**Topic:** `shipping-events`  
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

## Commands (Service Bus Queue)

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

All messages carry these `ApplicationProperties` on every Service Bus message:

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
4. **New event type** — add it to this catalog. Add a handler in every subscriber listed in the topic matrix. Update the processor's Subject dispatch switch.

---

## Dead Letter Queues

Each subscription has a dead-letter sub-queue at:
`{topic}/{subscription}/$deadletterqueue`

Messages land there after exceeding `MaxDeliveryCount` retries. The `messages.abandoned` OTel counter (tagged with `subject` and `service`) rises as messages approach the DLQ. Alert when this counter crosses your threshold.

Admin replay endpoints are available on each service at `POST /admin/events/replay/{eventId}`. See `docs/event-replay.md` for the replay guide.
