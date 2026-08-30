# Guided tour — one order, end to end

A ten-stop walk through the codebase following a single `POST /orders` from the browser to
`Shipped`. Each stop is a link into the real source plus the pattern it demonstrates — read
it top to bottom in ~10 minutes, or jump around. Best paired with the live demo at
[shop.emeraldleaf.dev](https://shop.emeraldleaf.dev) (`buyer1`/`buyer1`) and the
[architecture diagram](nextaurora-architecture.svg).

> Deep dives referenced along the way: [architecture.md](architecture.md) ·
> [performance-and-data-correctness.md](performance-and-data-correctness.md) ·
> [messaging-transport-selection.md](messaging-transport-selection.md) ·
> [cqrs-data-access.md](cqrs-data-access.md) · [vsa-vs-clean-architecture.md](vsa-vs-clean-architecture.md)

---

## 1 · The click — `frontend/src/features/ordering/`

[`place-order.ts`](../frontend/src/features/ordering/api/place-order.ts) builds the command
and POSTs it. Two things to notice: the client sends `unitPrice` but **the server ignores
it** (stop 3), and a Cloudflare Turnstile token rides an `X-Turnstile-Token` header — the
demo credentials are public, so JWT alone is not a bot gate
([`shared/turnstile.ts`](../frontend/src/shared/turnstile.ts), fail-closed server-side).
The response is **202 Accepted**, not 201 — placement is asynchronous, and the order row
itself is the tracking record ("long-running work belongs on the message bus", CLAUDE.md).

## 2 · The endpoint — [`OrderEndpoints.cs`](../OrderService/Endpoints/OrderEndpoints.cs)

A minimal-API route on a versioned group (`/api/v1/orders`). It reads the buyer's identity
from the **JWT `sub` claim**, never the request body, and hands a `PlaceOrderCommand` to
Wolverine. No controller, no MediatR-style handler interface — Wolverine's bus is the
abstraction (CLAUDE.md "Communication Patterns").

## 3 · The write side — [`PlaceOrder.cs`](../OrderService/Features/PlaceOrder.cs)

One file = the whole use case (vertical slice: command + validator + handler co-located).
The handler:

- calls CatalogService over **gRPC** to validate the lines and reserve stock — one batch
  round-trip (`ValidateLines`/`ReserveLines`), not N calls;
- prices the order from **the catalog's answer, not the client's numbers** — the
  server-controlled-fields rule (a `Price` field in a request body is a price-tampering bug);
- builds the `Order` aggregate via its factory (invariants enforced in the domain, not the
  handler);
- saves the row and publishes `OrderPlacedEvent` **in the same database transaction** — the
  transactional outbox. The event cannot be lost, even if RabbitMQ is down at commit time.

Diagram: [transactional-outbox.svg](transactional-outbox.svg). Why the publish must use the
enlisted `IMessageContext` and not a constructor-injected bus: the
[Wolverine 6 outbox war story](war-story-wolverine6-outbox-atomicity.md).

## 4 · The wire — [`MessagingTopology.cs`](../NextAurora.Contracts/Messaging/MessagingTopology.cs)

One **fanout exchange per event family** (`order-events`, `payment-events`,
`shipping-events`), one durable **queue per consumer** (`payment-orders`, `notify-orders`,
…). Names are shared constants because with auto-provisioning, a typo'd name is *silently
created as a new empty exchange* and the consumer starves with no error. Publishers declare
their consumers' queues too, so the full topology exists before the first publish — a fanout
exchange with zero bindings silently discards messages (#168).

## 5 · Payment reacts — [`OrderPlacedHandler.cs`](../PaymentService/Features/OrderPlacedHandler.cs)

PaymentService consumes `OrderPlacedEvent` from its own queue. Delivery is **at-least-once**,
so the handler is **idempotent** — a redelivery hits a guard and becomes a no-op instead of a
double charge. This isn't theoretical: the first night in production, a redelivered envelope
was rejected by Wolverine's durable inbox
([performance doc, "Container memory" section](performance-and-data-correctness.md) records
the observation). Success publishes `PaymentCompletedEvent`; failure publishes
`PaymentFailedEvent` through the exact same mechanics.

## 6 · Choreography, no conductor — [`ShippingService/Features/PaymentCompletedHandler.cs`](../ShippingService/Features/PaymentCompletedHandler.cs)

ShippingService reacts to the *same* `PaymentCompletedEvent` on its own queue and dispatches
a shipment; [`OrderService/Features/PaymentCompletedHandler.cs`](../OrderService/Features/PaymentCompletedHandler.cs)
marks the order `Paid` in parallel. **No orchestrator told anyone anything** — every service
just reacts to events. That's the choreography saga; the trade-offs vs orchestration are in
[architecture.md](architecture.md).

## 7 · The finish line — [`ShipmentDispatchedHandler.cs`](../OrderService/Features/ShipmentDispatchedHandler.cs)

`ShipmentDispatchedEvent` closes the loop: the order's state machine steps to `Shipped`
(states enforced by the aggregate, not by whoever calls it). NotificationService consumed
the placed and shipped events (and would consume a payment failure) from its `notify-*`
queues — it has no `PaymentCompletedEvent` handler, so that message is received and ignored
([`NotificationEventHandlers.cs`](../NotificationService/Features/NotificationEventHandlers.cs)) —
stateless, duplicate-tolerant, no database.

## 8 · The read side — [`GetOrderById.cs`](../OrderService/Features/GetOrderById.cs)

CQRS without ceremony: the query handler projects straight to a DTO **inside the
`IQueryable`** (`AsNoTracking` + `Select`), and the ownership check is **inside the SQL
`WHERE` clause** — `o.Id == id && o.BuyerId == requestingBuyerId`. A non-owner's row never
leaves the database, and the endpoint translates the null to a **404** (a 403 would leak
existence). The IDOR test proving buyer X cannot read buyer Y's order is required for every
scoped endpoint —
[`ProductAuthorizationTests.cs`](../tests/CatalogService.Tests.Integration/ProductAuthorizationTests.cs)
is the reference shape. Deep dive: [cqrs-data-access.md](cqrs-data-access.md).

## 9 · What you watched — [`SagaCanvas.tsx`](../frontend/src/features/orders/components/SagaCanvas.tsx)

The order page's canvas is a projection of stops 3–7: the real topology under production
names, the order's actual journey replayed hop by hop (only the pacing is theatrical — the
saga outruns human eyes), per-service colors, and the exactly-once-processing caption. The
narration strings live in [`saga.ts`](../frontend/src/features/orders/saga.ts) and change
when the backend does — the UI is documentation with a compile step.

## 10 · Break it — [`DemoEndpoints.cs`](../PaymentService/Endpoints/DemoEndpoints.cs)

The kill switch pauses the real Wolverine listening agent on `payment-orders`
(`PauseAsync(60s)` — auto-revive is Wolverine's own mechanism, so a visitor can't leave the
demo dead). While it's down, orders hold at `Placed` because the event is sitting in the
durable queue — the broker holds it until the consumer acks. Guardrails: mapped only under
`DemoMode=true` (404 otherwise), auth + rate limit + Turnstile. Proof of the whole
durability stack, live: [shop.emeraldleaf.dev](https://shop.emeraldleaf.dev), Act 3.

---

## Verify any of it yourself

- **Tests that prove the claims:** outbox + idempotency under redelivery —
  [`OrderSagaTests.cs`](../tests/OrderService.Tests.Integration/OrderSagaTests.cs) (real SQL
  Server via Testcontainers); IDOR — `ProductAuthorizationTests.cs`; kill-switch gating —
  [`DemoEndpointsTests.cs`](../tests/PaymentService.Tests.Integration/DemoEndpointsTests.cs);
  Turnstile fail-closed — [`TurnstileTests.cs`](../tests/OrderService.Tests.Integration/TurnstileTests.cs).
- **The API, live:** Scalar explorers and a token-in-one-curl quickstart — README
  ["Try the live API"](../README.md#try-the-live-api).
- **Run the whole thing locally:** README ["Getting Started"](../README.md#getting-started) —
  Aspire boots all five services + Postgres/SQL Server/RabbitMQ/Redis/Keycloak in Docker.
