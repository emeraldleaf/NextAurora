# NotificationService — code flow walkthrough

> **What this is.** A walk through the code paths in [NotificationService](../../NotificationService/) — the **smallest service in the system**. NotificationService is a stateless event-to-email pump: it consumes three events from other services and dispatches an email-like notification via a pluggable sender. No DB, no aggregate, no state worth protecting.
>
> **Architecture style:** Vertical Slice Architecture at its most minimal — **three files in `Features/`**, one in `Infrastructure/`, one `Program.cs`. **No `Domain/` folder at all.** This is the canonical example of "the pattern only earns its keep when there's something to protect": NotificationService has no invariants, no state machine, no aggregate root, so adding one would be pure ceremony. See [CLAUDE.md "Rich Domain Entities (when warranted)"](../../CLAUDE.md).
>
> **One flow to understand:** three events come in (one each from OrderService, PaymentService, ShippingService); each is translated to a `SendNotificationRequest` and dispatched through a single `SendNotificationHandler` that ends in `INotificationSender.SendAsync`.

---

## The flow — events in, email out

```mermaid
sequenceDiagram
    autonumber
    participant MQ as RabbitMQ<br/>(notify-orders / notify-payments /<br/>notify-shipping queues, each bound to<br/>its event family's fanout exchange)
    participant W as Wolverine consumer +<br/>ContextPropagation middleware
    participant EH as NotificationEventHandlers<br/>Features/NotificationEventHandlers.cs<br/>(3 static overloads, return commands)
    participant SH as SendNotificationHandler<br/>Features/SendNotification.cs
    participant Send as INotificationSender<br/>Features/SendNotification.cs<br/>(port)
    participant CS as ConsoleNotificationSender<br/>Infrastructure/<br/>(dev impl —<br/>SendGrid/Twilio/SES swap in prod)

    MQ->>W: OrderPlacedEvent<br/>OR PaymentFailedEvent<br/>OR ShipmentDispatchedEvent
    Note over W: ContextPropagation restores<br/>logger scope from envelope headers
    W->>EH: Handle(@event)
    Note over EH: pure event-to-command mapping —<br/>no I/O, no state, just string formatting<br/>(see "Why merged into one class" below)
    EH-->>W: returns SendNotificationRequest<br/>(Wolverine cascading message)
    W->>SH: HandleAsync(request, ct)

    Note over SH: minimal email-shape check —<br/>'@' present and length <= 254.<br/>Full RFC 5322 is over-validation —<br/>most RFC-valid addresses are<br/>still wrong in practice.

    alt invalid email shape
        SH-->>W: throw ArgumentException
        Note over W: GlobalExceptionHandler returns 400<br/>(if dispatched via HTTP) or<br/>Wolverine retries → DLQ (if via bus)
    else valid
        SH->>Send: SendAsync(email, subject, body, channel, ct)
        Send->>CS: dispatch
        Note over CS: dev — logs to console.<br/>prod adapter (SendGrid, Twilio,<br/>Amazon SES) is a DI swap —<br/>no handler code changes.
        CS-->>SH: ok
        SH-->>W: ok (NotificationsSent counter ++)
    end

    alt sender throws (transient failure)
        SH->>SH: log error
        SH-->>W: re-throw
        Note over W: Wolverine retry policy fires —<br/>re-throw is what makes that work.<br/>Swallowing the exception would<br/>silently drop the notification.
    end
```

**The three event-handler overloads** (all in [NotificationEventHandlers.cs](../../NotificationService/Features/NotificationEventHandlers.cs)):

| Event consumed | Source service | Email subject | Notes |
|---|---|---|---|
| `OrderPlacedEvent` | OrderService | "Order Received" | Buyer ID in event → placeholder email |
| `PaymentFailedEvent` | PaymentService | "Payment Failed" | Reflects raw gateway `Reason` (TODO: translate to user-friendly copy) |
| `ShipmentDispatchedEvent` | ShippingService | "Order Shipped" | Buyer ID in event → placeholder email (`BuyerId` denormalized from the Shipment aggregate) |

**Why all three overloads in one class.** Each handler is pure event-to-command mapping with no state and no branching beyond string formatting. Splitting them into separate classes would be uniform with the saga services (OrderService) but doesn't earn its keep here. If one grows real logic (lookup against a user-prefs cache, channel selection, A/B copy), promote it back to its own file at that point. The pattern explicitly allows this in [SendNotification.cs](../../NotificationService/Features/SendNotification.cs) — VSA puts what-changes-together in one place.

**Why no `IRecipientResolver` abstraction.** There used to be a stub `RecipientResolver` returning the same placeholder emails. It was deleted because the stub didn't enforce any contract that mattered — the abstraction wasn't earning its keep (per CLAUDE.md's "Interfaces earn their keep through consumer substitution"). When a real recipient lookup lands (gRPC to a user service, or a local cache hydrated from `UserCreated` events), introduce the seam at that point.

---

## Why no `Domain/` folder

NotificationService is the minimal counter-example to OrderService / PaymentService / ShippingService. Compare:

```mermaid
graph LR
    subgraph Saga["OrderService / PaymentService / ShippingService"]
        D1["Domain/<br/>aggregate + invariants +<br/>state guards"]
        F1["Features/<br/>commands + handlers"]
        I1["Infrastructure/<br/>EF + outbox"]
    end

    subgraph Notif["NotificationService"]
        F2["Features/<br/>3 event translators<br/>+ SendNotification"]
        I2["Infrastructure/<br/>1 sender impl"]
    end

    F1 --> D1
    F1 --> I1
    F2 --> I2

    style D1 fill:#dbeafe,stroke:#1e3a5f
    style F1 fill:#a7f3d0,stroke:#047857
    style I1 fill:#fed7aa,stroke:#c2410c
    style F2 fill:#a7f3d0,stroke:#047857
    style I2 fill:#fed7aa,stroke:#c2410c
```

What's missing on the right side is **a `Domain/` folder** — because there's nothing for one to hold:

- **No persisted state.** Nothing to put a concurrency token on, nothing to validate, no aggregate to load and mutate.
- **No business invariants.** The "rule" is "send the email" — that's not an invariant, it's a function.
- **No state machine.** The notification is a fire-and-forget event; there's no "Pending → Sent → Delivered" lifecycle the service tracks.

Adding a `Notification` entity with `Create()`, status enum, `private set` properties — would be the kind of speculative ceremony CLAUDE.md warns against. The shape matches the complexity; promote later if real domain rules emerge.

---

## File inventory

| Path | Purpose |
|---|---|
| [Features/NotificationEventHandlers.cs](../../NotificationService/Features/NotificationEventHandlers.cs) | Three static `Handle(@event)` overloads — each returns a `SendNotificationRequest` (Wolverine cascading) |
| [Features/SendNotification.cs](../../NotificationService/Features/SendNotification.cs) | The request record + `INotificationSender` port + `SendNotificationHandler` (all in one file — VSA) |
| [Infrastructure/ConsoleNotificationSender.cs](../../NotificationService/Infrastructure/ConsoleNotificationSender.cs) | Dev-time `INotificationSender` impl — logs instead of sending |
| [Infrastructure/DependencyInjection.cs](../../NotificationService/Infrastructure/DependencyInjection.cs) | DI wiring (registers the sender impl) |
| [Program.cs](../../NotificationService/Program.cs) | Composition root — Wolverine + RabbitMQ queue bindings (notify-orders / notify-payments / notify-shipping, plus the direct send-notification queue) + sender |

---

## See also

- [docs/code-flows/orderservice.md](orderservice.md) — produces `OrderPlacedEvent` (Flow input #1)
- [docs/code-flows/paymentservice.md](paymentservice.md) — produces `PaymentFailedEvent` (Flow input #2)
- [docs/code-flows/shippingservice.md](shippingservice.md) — produces `ShipmentDispatchedEvent` (Flow input #3)
- [CLAUDE.md "Rich Domain Entities (when warranted)"](../../CLAUDE.md) — the rule that lets this service skip the `Domain/` folder
- [docs/event-catalog.md](../event-catalog.md) — every event's shape and producer/consumer
