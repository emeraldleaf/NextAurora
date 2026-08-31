# Architecture Map

A structured, AI-consumable map of NextAurora. Read this when you need orientation:
which service does what, what shape it uses, where ports and entities live, and how
services talk to each other.

This file is canonical for **structure** (services, files, dependencies). For **rules**
(SOLID, DDD, performance), CLAUDE.md is canonical. When the codebase changes, regenerate
this map (the build commands at the bottom show how).

---

## Services

| Service | Shape | Database | Project layout |
|---|---|---|---|
| **CatalogService** | VSA | Postgres | Single project, `Features/` + `Domain/` + `Infrastructure/` + `Endpoints/` + `Grpc/` |
| **OrderService** | VSA | SQL Server | Single project, `Features/` + `Domain/` + `Infrastructure/` + `Endpoints/` |
| **PaymentService** | VSA | SQL Server | Single project, `Features/` + `Domain/` + `Infrastructure/` + `Endpoints/` |
| **ShippingService** | VSA | Postgres | Single project, `Features/` + `Domain/` + `Infrastructure/` + `Endpoints/` |
| **NotificationService** | VSA, no Domain | — | Single project, `Features/` + `Infrastructure/` only |
| **Storefront** | Static-file host | — | Frontend scaffold |
| **SellerPortal** | Static-file host | — | Frontend scaffold |

Shared infrastructure projects:

| Project | Role |
|---|---|
| **NextAurora.AppHost** | Aspire composition root — wires up containers, services, and Azure resources |
| **NextAurora.Contracts** | Event contracts (`Events/`), command DTOs (`Commands/`), shared DTOs (`DTOs/`) |
| **NextAurora.ServiceDefaults** | Shared middleware: `MapV1ApiGroup`, `GlobalExceptionHandler`, `AddNextAuroraContextPropagation`, `MigrateDatabaseAsync`, JWT/Keycloak wiring |

---

## Event flow

```
PlaceOrder (HTTP POST → OrderService)
    │
    │ 1. gRPC → CatalogService (validate + reserve stock per line)
    │
    │ 2. Order aggregate saved + OrderPlacedEvent staged in outbox (same tx)
    │
    └─→ OrderPlacedEvent (RabbitMQ exchange)
            ├─→ PaymentService consumes (OrderPlacedHandler → ProcessPayment)
            │       │
            │       │ Payment aggregate saved + PaymentCompletedEvent staged in outbox
            │       │
            │       └─→ PaymentCompletedEvent
            │               ├─→ OrderService consumes (PaymentCompletedHandler → mark Order paid)
            │               ├─→ ShippingService consumes (PaymentCompletedHandler → CreateShipment)
            │               │       │
            │               │       │ Shipment saved + ShipmentDispatchedEvent staged in outbox
            │               │       │
            │               │       └─→ ShipmentDispatchedEvent
            │               │               ├─→ OrderService consumes (ShipmentDispatchedHandler → mark Order shipped)
            │               │               └─→ NotificationService consumes (NotificationEventHandlers)
            │               └─→ NotificationService consumes (NotificationEventHandlers)
            │
            ├─→ PaymentFailedEvent (on payment decline)
            │       ├─→ OrderService consumes (PaymentFailedHandler → mark Order failed)
            │       └─→ NotificationService consumes
            │
            └─→ NotificationService consumes (NotificationEventHandlers)
```

Every publisher uses Wolverine's transactional outbox: the aggregate write and the event
stage commit in the same DB transaction. See CLAUDE.md "Transactional Outbox" + the
"Observability & Context Propagation" section for the wire-level details.

---

## Event contracts

All events live in `NextAurora.Contracts/Events/`:

- `OrderPlacedEvent` — published by OrderService
- `PaymentCompletedEvent` — published by PaymentService
- `PaymentFailedEvent` — published by PaymentService
- `ShipmentDispatchedEvent` — published by ShippingService

RabbitMQ topology: one fanout exchange per event type, one queue per consumer bound to it.
Convention: `{consumer}-{source-events}` (e.g. `notify-orders` = NotificationService consuming
`order-events`). Wolverine declares + AutoProvisions the exchanges/queues/bindings.

---

## Ports (interfaces consumed by handlers)

VSA services keep ports in `Domain/` (where the interface lives next to the aggregate it
operates on); the Clean service splits them between Domain and Application.

### OrderService — `OrderService/Domain/`
- `ICatalogClient` — gRPC client for CatalogService (product validation, stock reservation)
- No repository and no `IEventPublisher`: handlers take `OrderDbContext` directly and publish
  through a method-injected `IMessageContext` (enlisted in the outbox transaction).

### PaymentService — `PaymentService/Domain/`
- `IPaymentGateway` — external payment provider port (Stripe stub today)
- `IEventPublisher` — thin Wolverine shim, used only for fire-and-forget re-publishes with no
  entity write to bind to; transactional publishes go through `IMessageContext`.
- No repository: handlers take `PaymentDbContext` directly.

### ShippingService — `ShippingService/`
- No ports. Handlers take `ShippingDbContext` directly and publish through `IMessageContext`.

### NotificationService — `NotificationService/Features/`
- `INotificationSender` — declared beside its consumer in `SendNotification.cs`; console
  implementation today, substituted in unit tests. No persistence, no aggregates, no Domain folder.

### CatalogService — `CatalogService/Domain/` (interfaces) + `CatalogService/Features/` (handlers)
- `IProductCache` — read-side cache port (HybridCache-backed: L1 in-process + L2 Redis)
- No repositories: handlers take `CatalogDbContext` directly.

Per CLAUDE.md "Interfaces earn their keep through consumer substitution": `ICatalogClient`,
`IPaymentGateway`, and `INotificationSender` are substituted in tests today. `IProductCache`
and `IEventPublisher` are not — each has exactly one implementation and no test double, so
both are open items under that rule rather than examples of it (CLAUDE.md "Interfaces earn
their keep").
Speculative interfaces have been deleted (see the deleted `IRecipientResolver` /
`StubRecipientResolver` in NotificationService — kept here as a cautionary footnote).

---

## Aggregates

| Service | Aggregate | Concurrency token |
|---|---|---|
| OrderService | `Order` (with `OrderLine` child entities) | SQL Server `RowVersion` shadow column |
| PaymentService | `Payment`, `Refund` | SQL Server `RowVersion` |
| ShippingService | `Shipment` (with `TrackingEvent` children) | Postgres `xmin` |
| CatalogService | `Product`, `Category` | Postgres `xmin` |
| NotificationService | — (no persistence) | — |

Every aggregate uses factory methods (`static Create(...)`) with validation, not public
constructors. Private setters. State changes go through methods (e.g. `Order.MarkPaid()`).

---

## Endpoints

All five services register endpoints in `Endpoints/{Service}Endpoints.cs`.

All endpoints use `MapV1ApiGroup("Tag", "resource")` from ServiceDefaults — that returns
a `RouteGroupBuilder` rooted at `/api/v1/resource` with the version + tag applied. Never
hand-roll `NewVersionedApi().MapGroup().HasApiVersion()` chains; drift across services is
the failure mode.

---

## Cross-service communication

- **REST (HTTP)** — frontend ↔ services only. URL-segment versioned `/api/v1/...`.
- **gRPC (sync)** — OrderService → CatalogService for real-time product validation + stock reservation. Versioned via `.proto` `package` declarations.
- **RabbitMQ (async)** — all workflow events. Wolverine transport + transactional outbox.

---

## CatalogService internals (single-project VSA, same as the other services)

```
CatalogService/
├── Program.cs            ← Composition root: DI, Wolverine, gRPC, OpenAPI/Scalar, middleware
├── Endpoints/            ← Minimal API endpoint registrations
├── Features/             ← One file per slice: command + validator + handler co-located
├── Domain/               ← Entities, enums, port interfaces (IProductCache)
├── Infrastructure/       ← EF Core (CatalogDbContext), HybridCache impl, migrations
├── Grpc/                 ← CatalogGrpcService (server for OrderService's client)
└── Protos/               ← catalog.proto contract
```

Layer boundaries are folder + namespace conventions enforced by
`tests/NextAurora.ArchitectureTests` (NetArchTest: Domain references no EF/Wolverine/etc.),
not by project references — the multi-project split was collapsed in PR #31.

---

## Demo deployment

A legacy single-service Catalog demo is deployed to Fly.io at https://catalog-api-demo.fly.dev (the full-stack demo lives on the VPS — see docs/deployed-demo.md). Single Fly
Machine in `lax` region, auto-stops when idle. `DemoMode` config flag gates Scalar UI,
OpenAPI exposure, skip-HTTPS-redirect, and migrate-on-startup in non-Development
environments. Redis registration is conditional on a `cache` connection string so
HybridCache degrades to L1-only when there's no Redis. See `docs/demo-deployment.md`.

Other services are not currently deployed.

---

## Regenerate this map

When services / aggregates / events change materially, refresh this file. The structural
parts come from these commands:

```bash
# Services
find . -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*/tests/*" | sort

# Features (VSA services)
for svc in OrderService PaymentService ShippingService NotificationService; do
    echo "--- $svc ---"
    ls $svc/Features/
done

# Events
ls NextAurora.Contracts/Events/

# Ports
grep -rln '^public interface I' --include='*.cs' OrderService PaymentService ShippingService CatalogService NotificationService
```

For the *narrative* parts (event flow, why a service is shaped a certain way), update
by hand — those don't regenerate cleanly from grep.

---

## Use cases for this map

- **`architecture-reviewer` agent** loads this to orient itself before reviewing a target.
- **New session starts** — a human or AI assistant skims this to find their bearings.
- **Onboarding** — pair this with CLAUDE.md and STATUS.md for the three-file orientation set: STATUS.md ("where we are right now"), CLAUDE.md ("how we do things"), architecture-map.md ("what's where").
