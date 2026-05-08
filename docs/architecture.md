# NextAurora Architecture

## Table of Contents

- [System Overview](#system-overview)
- [Service Architecture](#service-architecture)
- [Communication Patterns](#communication-patterns)
- [Data Architecture](#data-architecture)
- [Event-Driven Architecture](#event-driven-architecture)
- [Domain Model](#domain-model)
- [Infrastructure & Orchestration](#infrastructure--orchestration)
- [Cross-Cutting Concerns](#cross-cutting-concerns)
- [Design Patterns](#design-patterns)
- [Future Considerations](#future-considerations)

---

## System Overview

NextAurora is a distributed e-commerce platform built as a microservices architecture. Each service owns its data, communicates asynchronously via events for workflows, and uses gRPC for synchronous queries between services.

```
                         +-------------------+     +-------------------+
                         |    Storefront     |     |   SellerPortal    |
                         |  (Blazor WASM)    |     |   (scaffold)      |
                         +--------+----------+     +--------+----------+
                                  |                         |
                           REST API (HTTP)            REST API (HTTP)
                                  |                         |
                 +----------------+-------------------------+----------------+
                 |                |                         |                |
        +--------v-------+ +-----v--------+    +-----------v--+ +-----------v--+
        | CatalogService | | OrderService |    | CatalogService | OrderService |
        +--------+-------+ +-----+--------+    +--------------+ +-------------+
                 ^                |
                 |     gRPC (sync product validation)
                 +<---------------+
                                  |
                    +-------------v--------------+
                    |     Async Messaging         |
                    |  (Topics & Subscriptions)   |
                    +---+------+------+------+---+
                        |      |      |      |
                  +-----v-+ +--v---+ +v------v+ +--------+
                  | Order  | |Pay-  | |Ship-   | |Notifi- |
                  |Service | |ment  | |ping    | |cation  |
                  |        | |Svc   | |Service | |Service |
                  +--------+ +------+ +--------+ +--------+
                  SQL Server  SQL Svr  PostgreSQL  Stateless
```

> **Transport choice is environmental, not architectural.** Locally and in CI, the async-messaging layer is Azure Service Bus run as an Aspire-managed emulator. In production on AWS, it's Amazon SNS + SQS. Handlers and domain code don't change — see the [Deployment](#deployment) section.

## Service Architecture

Each backend service follows a **Clean Architecture** layered structure:

```
ServiceName/
  ServiceName.Domain/          # Entities, enums, repository interfaces
  ServiceName.Application/     # Commands, queries, handlers (Wolverine)
  ServiceName.Infrastructure/  # EF Core, repositories, messaging, external gateways
  ServiceName.Api/             # ASP.NET Core host, endpoints, DI composition
```

### Layer Responsibilities

| Layer | Responsibility | Dependencies |
|-------|---------------|-------------|
| **Domain** | Entities, value objects, domain interfaces, business rules | None |
| **Application** | CQRS commands/queries, Wolverine handler POCOs, application interfaces | Domain |
| **Infrastructure** | EF Core DbContext, repositories, Service Bus, external gateways | Domain, Application |
| **Api** | HTTP endpoints, gRPC services, DI registration, host configuration | All layers |

### Service Breakdown

#### CatalogService
- **Purpose:** Product catalog management
- **Database:** PostgreSQL
- **Exposes:** REST API (external) + gRPC server (internal)
- **Entities:** Product, Category
- **Key Feature:** gRPC service for real-time product validation by OrderService

#### OrderService
- **Purpose:** Order lifecycle management
- **Database:** SQL Server
- **Exposes:** REST API (external)
- **Consumes:** CatalogService via gRPC, PaymentService and ShippingService events via Service Bus
- **Entities:** Order, OrderLine
- **Key Feature:** Orchestrates order state through event-driven saga

#### PaymentService
- **Purpose:** Payment processing
- **Database:** SQL Server
- **Exposes:** REST API (external)
- **Consumes:** OrderService events via Service Bus
- **Entities:** Payment, Refund
- **Key Feature:** Stripe gateway integration (anti-corruption layer)

#### ShippingService
- **Purpose:** Shipment creation and tracking
- **Database:** PostgreSQL
- **Exposes:** REST API (external)
- **Consumes:** PaymentService events via Service Bus
- **Entities:** Shipment, TrackingEvent
- **Key Feature:** Auto-generates tracking numbers and assigns carriers

#### NotificationService
- **Purpose:** Customer notifications
- **Database:** None (stateless)
- **Consumes:** OrderService and ShippingService events via Service Bus
- **Entities:** NotificationRequest (in-memory)
- **Key Feature:** Pluggable notification sender (console in dev, email/SMS in production)

---

## Communication Patterns

### 1. Event-Driven Messaging (Async)

Used for all workflow/saga communication between services. Azure Service Bus provides at-least-once delivery with topic/subscription pub-sub model.

**When to use:** State changes that trigger downstream workflows (order placed, payment completed, shipment dispatched).

### 2. gRPC (Sync)

Used for synchronous request/reply queries between services where the caller needs an immediate response.

**Current usage:** OrderService calls CatalogService via gRPC to validate product availability and pricing before creating an order.

**Why gRPC over REST for this:**
- Binary serialization (Protocol Buffers) is faster than JSON
- Strong typing via .proto contract
- HTTP/2 multiplexing
- Built-in code generation

### 3. REST APIs (External)

Used for frontend-to-service communication. ASP.NET Core Minimal APIs with OpenAPI documentation, URL-segment versioned (`/api/v{version}/...`) via `Asp.Versioning.Http`.

**When to use:** Client-facing endpoints accessed by Storefront and SellerPortal.

**Versioning:** Default version is `1.0`; the version segment is required in the URL. Adding a v2 endpoint is a side-by-side handler with `.HasApiVersion(new ApiVersion(2, 0))` and a separate route — old v1 callers keep working without changes.

### Communication Matrix

| From | To | Protocol | Purpose |
|------|----|----------|---------|
| Storefront | CatalogService | REST | Browse/search products |
| Storefront | OrderService | REST | Place/view orders |
| SellerPortal | CatalogService | REST | Manage products |
| SellerPortal | OrderService | REST | View orders |
| OrderService | CatalogService | **gRPC** | Validate products during order placement |
| OrderService | PaymentService | **Service Bus** | OrderPlacedEvent triggers payment |
| PaymentService | OrderService | **Service Bus** | PaymentCompletedEvent updates order |
| PaymentService | ShippingService | **Service Bus** | PaymentCompletedEvent triggers shipment |
| PaymentService | NotificationService | **Service Bus** | PaymentFailedEvent triggers buyer notification |
| ShippingService | OrderService | **Service Bus** | ShipmentDispatchedEvent updates order |
| OrderService | NotificationService | **Service Bus** | OrderPlacedEvent triggers notification |
| ShippingService | NotificationService | **Service Bus** | ShipmentDispatchedEvent triggers notification |

---

## Data Architecture

### Polyglot Persistence

Each service owns its database. No service accesses another service's database directly.

| Service | Database | Rationale |
|---------|----------|-----------|
| CatalogService | **PostgreSQL** | Read-heavy workload, JSONB support for flexible product attributes |
| OrderService | **SQL Server** | Transaction-heavy, strong ACID guarantees for order state |
| PaymentService | **SQL Server** | Financial transactions require strict consistency |
| ShippingService | **PostgreSQL** | Read-heavy tracking queries, array support for events |
| NotificationService | None | Stateless, fire-and-forget |

### Database Schemas

#### catalog-db (PostgreSQL)

| Table | Columns |
|-------|---------|
| **Products** | Id, Name, Description, Price, Currency, CategoryId (FK), SellerId, StockQuantity, IsAvailable, CreatedAt, UpdatedAt |
| **Categories** | Id, Name, Description |

#### orders-db (SQL Server)

| Table | Columns |
|-------|---------|
| **Orders** | Id, BuyerId, Status, TotalAmount, Currency, PlacedAt, PaidAt, ShippedAt |
| **OrderLines** | Id, OrderId (FK), ProductId, ProductName, Quantity, UnitPrice |

#### payments-db (SQL Server)

| Table | Columns |
|-------|---------|
| **Payments** | Id, OrderId, Amount, Currency, Status, Provider, ExternalTransactionId, CreatedAt, CompletedAt, FailureReason |
| **Refunds** | Id, PaymentId, Amount, Reason, Status, CreatedAt |

#### shipping-db (PostgreSQL)

| Table | Columns |
|-------|---------|
| **Shipments** | Id, OrderId, Carrier, TrackingNumber, Status, CreatedAt, DispatchedAt, DeliveredAt |
| **TrackingEvents** | Id, ShipmentId (FK), Description, Status, OccurredAt |

---

## Event-Driven Architecture

### Message Topology

```
Azure Service Bus
  |
  +-- Topic: order-events
  |     +-- Subscription: payment-orders-sub      -> PaymentService
  |     +-- Subscription: notify-orders-sub       -> NotificationService
  |
  +-- Topic: payment-events
  |     +-- Subscription: order-payments-sub      -> OrderService
  |     +-- Subscription: shipping-payments-sub   -> ShippingService
  |     +-- Subscription: notify-payments-sub     -> NotificationService
  |
  +-- Topic: shipping-events
  |     +-- Subscription: order-shipping-sub      -> OrderService
  |     +-- Subscription: notify-shipping-sub     -> NotificationService
  |
  +-- Queue: send-notification                    -> NotificationService
```

**Subscription naming convention: `{consumer}-{source-events}-sub`.** Aspire 13 enforces globally unique subscription names within a bus namespace (the per-topic scoping behavior of Aspire 9 was dropped). Including the source-events suffix in the name keeps it readable and unique. The strings here must match the `ListenToAzureServiceBusSubscription("{topic}/{sub}")` calls in each service's `Program.cs`.

### Event Contracts (NextAurora.Contracts)

| Event | Publisher | Subscribers | Payload |
|-------|-----------|-------------|---------|
| **OrderPlacedEvent** | OrderService | PaymentService, NotificationService | OrderId, BuyerId, TotalAmount, Currency, Lines[] |
| **PaymentCompletedEvent** | PaymentService | OrderService, ShippingService | PaymentId, OrderId, Amount, Provider, CompletedAt |
| **PaymentFailedEvent** | PaymentService | OrderService, NotificationService | PaymentId, OrderId, BuyerId, Reason, FailedAt |
| **ShipmentDispatchedEvent** | ShippingService | OrderService, NotificationService | ShipmentId, OrderId, Carrier, TrackingNumber, DispatchedAt |
| **SendNotificationCommand** | Any service | NotificationService | RecipientId, Email, Subject, Body, Channel |

### Order Lifecycle Saga

```
  [Placed] ---OrderPlacedEvent---> PaymentService processes payment
      |
      |  <---PaymentCompletedEvent---          <---PaymentFailedEvent---
      v                                                    v
  [Paid]                                          [PaymentFailed] (terminal)
      |
      | ---PaymentCompletedEvent---> ShippingService creates shipment
      |
      |  <---ShipmentDispatchedEvent---
      v
  [Shipped]
      |
      v
  [Delivered]
```

This is a **choreography-based saga** — each service reacts to events independently. There is no central orchestrator.

---

## Domain Model

### Order Aggregate

```
Order (Aggregate Root)
  - Id: Guid
  - BuyerId: Guid (must not be empty)
  - Status: OrderStatus [Placed | Paid | Shipped | Delivered | Cancelled | PaymentFailed]
  - TotalAmount: decimal (calculated from lines)
  - Currency: string (required, 3 chars)
  - PlacedAt, PaidAt, ShippedAt: DateTime
  - Lines: IReadOnlyList<OrderLine> (private backing field, encapsulated)

  Invariants (enforced in Create):
  - BuyerId must not be empty
  - Currency is required
  - Must have at least one line

  Business Rules:
  - Can only mark as Paid if status is Placed
  - Can only mark as PaymentFailed if status is Placed (terminal state)
  - Can only mark as Shipped if status is Paid
  - Cannot cancel if Shipped or Delivered
  - Error messages do not expose internal state

OrderLine (Entity)
  - ProductId (must not be empty), ProductName (required)
  - Quantity (must be > 0), UnitPrice (must be >= 0)
```

### Product Aggregate

```
Product (Aggregate Root)
  - Id, Name (required), Description, Price (must be > 0), Currency (required)
  - CategoryId (must not be empty) -> Category
  - SellerId (required), StockQuantity (must be >= 0), IsAvailable

  Invariants (enforced in Create and UpdateDetails):
  - Name must not be empty
  - Price must be positive
  - Stock must be non-negative
  - CategoryId and SellerId must not be empty

  Business Rules:
  - IsAvailable is derived from StockQuantity > 0
  - Stock adjustment validates non-negative quantity
```

### Payment Aggregate

```
Payment (Aggregate Root)
  - Id, OrderId (must not be empty), Amount (must be > 0), Currency (required)
  - Status: PaymentStatus [Pending | Completed | Failed | Refunded]
  - Provider (required), ExternalTransactionId

  Invariants (enforced in Create):
  - OrderId must not be empty
  - Amount must be positive
  - Currency and Provider are required

  Business Rules:
  - Can only complete if status is Pending
  - Can only fail if status is Pending

Refund (Entity)
  - PaymentId, Amount, Reason
  - Status: RefundStatus [Pending | Processed | Failed]
```

### Shipment Aggregate

```
Shipment (Aggregate Root)
  - Id, OrderId, Carrier, TrackingNumber
  - Status: ShipmentStatus [Created | Dispatched | InTransit | Delivered]
  - TrackingEvents: List<TrackingEvent>

TrackingEvent (Entity)
  - Description, Status, OccurredAt
```

---

## Infrastructure & Orchestration

### .NET Aspire (AppHost)

The AppHost project orchestrates the entire distributed system for local development:

```csharp
// Infrastructure containers
PostgreSQL  -> catalog-db, shipping-db
SQL Server  -> orders-db, payments-db
Redis       -> cache (CatalogService)
Service Bus -> messaging (all topics, subscriptions, queues)
App Insights -> observability

// Service references
CatalogService  -> catalog-db, cache, insights
OrderService    -> orders-db, messaging, catalog-service (gRPC), insights
PaymentService  -> payments-db, messaging, insights
ShippingService -> shipping-db, messaging, insights
NotificationService -> messaging, insights
Storefront      -> catalog-service, order-service
SellerPortal    -> catalog-service, order-service
```

### Service Defaults (NextAurora.ServiceDefaults)

All services inherit shared infrastructure configuration:

- **OpenTelemetry:** Logging (formatted messages + scopes), metrics (ASP.NET Core, HTTP, runtime), tracing (ASP.NET Core, HTTP, gRPC)
- **Service Discovery:** Automatic service-to-service resolution via Aspire
- **HTTP Resilience:** Standard resilience handler (retries, circuit breaker, timeout)
- **Health Checks:** `/health` (readiness) and `/alive` (liveness)
- **Global Exception Handler:** `GlobalExceptionHandler` converts exceptions to RFC 7807 ProblemDetails responses with trace IDs. Handles `ValidationException` (400), `ArgumentException` (400), `InvalidOperationException` (409), and unhandled exceptions (500). Internal details are logged server-side, never exposed to clients.
- **HTTPS Redirection:** Enforced in production environments across all services

### gRPC Setup

**Server (CatalogService):**
- Proto file: `CatalogService.Api/Protos/catalog.proto`
- Service: `CatalogGrpcService` wraps existing Wolverine handler POCOs via `IMessageBus.InvokeAsync<T>()`
- Registered via `builder.Services.AddGrpc()` and `app.MapGrpcService<CatalogGrpcService>()`

**Client (OrderService):**
- References proto file with `GrpcServices="Client"`
- Registered via `AddGrpcClient<CatalogGrpc.CatalogGrpcClient>()` with Aspire service discovery URL
- Wrapped by `GrpcCatalogClient` implementing `ICatalogClient` application interface

---

## Cross-Cutting Concerns

### Observability
- **Tracing:** OpenTelemetry distributed traces across all services (ASP.NET Core, HTTP client, gRPC client, `Azure.Messaging.ServiceBus`). Service Bus processors create consumer spans via `ActivitySource("NextAurora.Messaging")` so the full event chain is visible in the Aspire dashboard and any OTLP backend.
- **Context Propagation:** Every HTTP request and Service Bus message carries three identifiers — `CorrelationId`, `UserId`, `SessionId` — stamped by `CorrelationIdMiddleware` (HTTP) or each processor (Service Bus) into `Activity` baggage and `logger.BeginScope()`. All log lines produced by any handler automatically include these fields. See [docs/context-propagation.md](context-propagation.md).
- **Wolverine Pipeline Logging:** Wolverine's built-in `Policies.LogMessageStarting()` logs handler name and elapsed time. `ContextPropagationMiddleware` (in ServiceDefaults) opens a `logger.BeginScope()` so all handler log lines carry `CorrelationId`/`UserId`/`SessionId`.
- **Metrics:** Business counters via `Meter("NextAurora")` in `NovaCraftMetrics`: `orders.placed`, `payments.processed` (tag: `outcome`), `shipments.dispatched`, `notifications.sent` (tag: `channel`), `messages.abandoned` (tags: `subject`, `service`). Exported via OTLP; visible in Aspire Metrics dashboard.
- **Logging:** Structured logging with OpenTelemetry export
- **Dashboard:** Aspire dashboard shows all services, traces, logs, and metrics in development

### Resilience
- Standard resilience handler on all HTTP clients (retries, circuit breaker, timeout, rate limiting)
- gRPC calls benefit from HTTP client resilience via service discovery

### Authentication & Authorization
- **JWT Bearer** wired in `NextAurora.ServiceDefaults.AddJwtBearerAuthentication()`. Validates issuer, audience, lifetime; reads authority from `Authentication:Authority` (fallback `Keycloak:Url`), audience from `Authentication:Audience` (default `nextaurora-api`).
- **Keycloak** runs as an Aspire-managed container; `nextaurora-realm` is imported from `realms/nextaurora-realm.json` and injected into each service via `WithReference(realm, configurationPrefix: "Keycloak")`.
- **Claim mapping:** `NameClaimType = "preferred_username"`, `RoleClaimType = "realm_access.roles"`.
- **Endpoint protection:** `.RequireAuthorization()` on Catalog writes (`POST`/`PUT /api/v1/products`), the entire `/api/v1/orders` group, `/api/v1/payments/process`, the entire `/api/v1/shipments` group. Public reads (`GET /api/v1/products`) remain anonymous.
- **Buyer-scope checks:** `POST /api/v1/orders` and `GET /api/v1/orders/buyer/{buyerId}` reject when the JWT `sub` claim doesn't match the route/body buyer ID (returns 403).
- **No-op fallback:** if no `Authentication:Authority` and no `Keycloak:Url` are present, ServiceDefaults registers vanilla `AddAuthentication()`/`AddAuthorization()` so middleware doesn't crash, but `.RequireAuthorization()` endpoints will return 401 (no scheme to validate against).

### Input Validation
- **FluentValidation:** All commands have corresponding validator classes (e.g., `CreateProductCommandValidator`, `PlaceOrderCommandValidator`, `ProcessPaymentCommandValidator`)
- **Input Validation:** All commands have `FluentValidation` validator classes. `opts.UseFluentValidation()` in Wolverine's pipeline runs validators before handlers, throwing `ValidationException` with structured errors on failure.
- **Domain Guard Clauses:** Factory methods (`Create()`) and mutation methods enforce invariants with `ArgumentException`/`ArgumentOutOfRangeException`

### Error Handling
- **Global Exception Handler:** `GlobalExceptionHandler` in ServiceDefaults converts all unhandled exceptions to RFC 7807 ProblemDetails with trace IDs
- **No State Leakage:** Error messages returned to clients are generic; details (product IDs, stock levels, internal state) are logged server-side only
- **Structured Errors:** Validation failures return grouped errors by property name

### Code Quality
- **Static Analysis:** Meziantou.Analyzer, SonarAnalyzer.CSharp, Roslynator.Analyzers
- **Build Config:** `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisMode=All`
- **Coding Standards:** `.editorconfig` with naming conventions and severity rules
- **Package Management:** Central Package Management via `Directory.Packages.props`
- **CI/CD:** GitHub Actions pipeline (restore, build, test)

---

## CQRS & Data Access

NextAurora implements CQRS at the application layer. Commands and queries are separate record types with dedicated Wolverine handler POCOs. Query handlers return DTOs and never modify state. Command handlers mutate domain entities and publish events. See [docs/cqrs-data-access.md](cqrs-data-access.md) for the full handler inventory and data access analysis.

### Query Path

```
HTTP Request → Endpoint → IMessageBus.InvokeAsync<TResult>(query)
  → QueryHandler.Handle() → Repository (read-only) → Domain Entity → DTO
```

Query handlers (6 total across Catalog, Order, and Shipping) map domain entities to DTOs before returning. They never call `SaveChangesAsync()`, publish events, or modify entity state.

### Command Path

```
HTTP Request or Service Bus Message → IMessageBus.InvokeAsync<TResult>(command)
  → CommandHandler.Handle() → Repository (read + write) → Domain Entity → Event Published
```

Command handlers create or mutate entities, persist changes, and publish domain events. Event handlers follow the same pattern — they read an entity, mutate its state via domain methods, and save.

### EF Core Change Tracking Strategy

Read and write paths share the same repository interfaces. Some `GetByIdAsync` methods are called by both query handlers (read-only) and command/event handlers (need tracking for subsequent updates). `AsNoTracking()` is applied selectively:

**Read-only methods** (`AsNoTracking` applied) — exclusively called from query handlers:
- `ProductRepository`: `GetAllAsync`, `GetByCategoryAsync`, `SearchAsync`
- `CategoryRepository`: `GetByIdAsync`, `GetAllAsync`
- `OrderRepository`: `GetByBuyerIdAsync`

**Shared methods** (tracking preserved) — called by command or event handlers that mutate and save:
- `ProductRepository.GetByIdAsync` — `UpdateProductHandler`, `ReserveStockHandler`
- `OrderRepository.GetByIdAsync` — `PaymentCompletedHandler`, `PaymentFailedHandler`, `ShipmentDispatchedHandler`
- `PaymentRepository.GetByOrderIdAsync` — `ProcessPaymentHandler`
- `ShipmentRepository.GetByOrderIdAsync` — `CreateShipmentHandler`

Adding `AsNoTracking()` to shared methods would break the read-then-mutate-then-save pattern because EF Core wouldn't detect changes on untracked entities. Full read/write repository separation (Interface Segregation) is a future consideration.

---

## Design Patterns

| Pattern | Implementation |
|---------|---------------|
| **CQRS** | Separate command and query objects; Wolverine handler POCOs discovered by convention (`Handle()` method) |
| **Repository** | EF Core repositories behind domain interfaces |
| **Domain-Driven Design** | Aggregates with factory methods, guard clauses, encapsulated collections (`IReadOnlyList`), no public setters |
| **Validation Pipeline** | FluentValidation + Wolverine `opts.UseFluentValidation()` for pre-handler validation |
| **Event-Driven Architecture** | Azure Service Bus pub/sub with topic/subscription model |
| **Choreography Saga** | Order lifecycle managed through event chain across services |
| **Anti-Corruption Layer** | StripePaymentGateway isolates domain from external payment API |
| **Service Discovery** | Aspire-based automatic service resolution |
| **Strangler Fig (Ready)** | REST + gRPC endpoints allow incremental migration |

---

## Future Considerations

### Implemented
- **Input Validation** - FluentValidation on all commands via `opts.UseFluentValidation()` in Wolverine pipeline
- **Wolverine Pipeline Logging** - Wolverine built-in `LogMessageStarting` + `ContextPropagationMiddleware` scope covers timing, correlation ID, elapsed time, and outcome
- **Context Propagation** - `CorrelationId`, `UserId`, `SessionId` flow through HTTP and Service Bus; see [docs/context-propagation.md](context-propagation.md)
- **Domain Invariants** - Guard clauses in all entity factory methods
- **Global Exception Handling** - ProblemDetails responses, no internal state leakage
- **Encapsulated Aggregates** - `IReadOnlyList` collections, private backing fields
- **HTTPS Redirection** - Enforced in production
- **Idempotent Event Handling** - Status guards in all event handlers; GetByOrderId checks prevent duplicate processing
- **Transactional Outbox** - Wolverine transactional outbox in Order, Payment, Shipping. Outgoing events persist to a `wolverine` schema in the same DB transaction as the entity write; background dispatcher flushes to Service Bus. Concurrency-retry policy on `DbUpdateConcurrencyException` (3 attempts, 50/100/250ms backoff). See [docs/performance-and-data-correctness.md](performance-and-data-correctness.md).
- **Optimistic Concurrency Tokens** - Postgres `xmin` (Catalog Product/Category, Shipping Shipment) and SQL Server `RowVersion` (Order, Payment, Refund) shadow properties. Last-write-wins is no longer possible.
- **EF Core Migrations** - Initial migrations for all four DB services (Catalog, Order, Payment, Shipping). `IDesignTimeDbContextFactory<T>` per context for `dotnet ef` tooling. `MigrateDatabaseAsync<T>()` runs at app startup in development; production should run as a separate deploy step.
- **Authentication & Authorization** - JWT Bearer authentication wired in `NextAurora.ServiceDefaults` (`AddJwtBearerAuthentication()`); identity provider is **Keycloak** (Aspire-managed container, `nextaurora-realm` imported from `realms/nextaurora-realm.json`). `AddJwtBearer` validates issuer, audience, lifetime; claim mapping uses `preferred_username` → name and `realm_access.roles` → role. `.RequireAuthorization()` on every state-changing endpoint and buyer-scoped reads (Catalog write endpoints, all of `/api/v1/orders`, `/api/v1/payments/process`, all of `/api/v1/shipments`). Buyer-scope endpoints additionally verify the JWT `sub` claim matches the route/body buyer ID. `GET /api/v1/products` remains anonymous.
- **API Versioning** - URL-segment versioning via `Asp.Versioning.Http`. Routes follow `/api/v{version:apiVersion}/...` with the version required in the URL (`AssumeDefaultVersionWhenUnspecified = false`). Default version is `1.0`; `Asp.Versioning.Mvc.ApiExplorer` integrates with OpenAPI so versioned endpoints show up under group `v1` in Swagger. Configured globally in `AddServiceDefaults()` so every service inherits the same policy. gRPC is versioned separately via `.proto` `package` (out of scope here).
- **Dead Letter Queue Processing** - `messages.abandoned` metric counter on all processors. Replay/audit available via Wolverine's `IMessageStore` API or by querying the `wolverine` schema directly.

### Not Yet Implemented
- **API Gateway** - Centralized routing, rate limiting, auth
- **Saga Compensation** - Rollback logic for failed payments/shipments
- **Distributed Caching** - Redis caching strategy for CatalogService
- **Frontend Implementation** - Storefront and SellerPortal business logic
- **Integration Tests** - Testcontainers-based service testing
- **Order Cancellation Flow** - Cancel event and compensation logic
- **Production migration deployment step** - In dev, `MigrateDatabaseAsync<T>()` runs at startup; production should run migrations as a separate deploy step (not in-process) to avoid races between replicas. Tooling exists; deploy automation does not.

---

## Deployment

NextAurora is built transport-agnostic via Wolverine — handlers depend on `IMessageBus` and `Envelope`, not on transport-specific types. That means the local-dev choice of Azure Service Bus (run as an Aspire emulator) does not lock the app into Azure. The intended production deployment is **Amazon Web Services**, with **SNS + SQS** as the messaging backbone.

### Why SNS + SQS over RabbitMQ

A common confusion: "AWS deployment" doesn't imply RabbitMQ. The AWS-native equivalent of Azure Service Bus is the SNS + SQS pair — SNS provides the publish-subscribe topic surface; SQS provides per-subscriber queues.

| Concern | Recommended | Alternative | Notes |
|---|---|---|---|
| Topic + per-subscriber queues | SNS topic → SQS queues | RabbitMQ on Amazon MQ | SNS+SQS is fully managed, IAM/VPC/CloudWatch native, pay-per-message. |
| Cross-cloud portability | — | RabbitMQ on Amazon MQ | Pick this only if running the same broker on AWS, Azure, GCP, on-prem matters more than first-class AWS integration. |
| Event-streaming workloads | Amazon MSK (Kafka) | — | Out of scope for our saga model — different mental model (log-based). |
| Higher-level event routing | Amazon EventBridge | — | Wolverine support is limited; not a fit for the per-subscription idempotency model we use. |

### 1:1 topology mapping

| NextAurora today (Azure) | AWS equivalent |
|---|---|
| Topic `order-events` | SNS topic `order-events` |
| Subscription `payment-orders-sub` | SQS queue `payment-orders-sub` (subscribed to the topic) |
| Subscription `notify-orders-sub` | SQS queue `notify-orders-sub` (subscribed to the topic) |
| Topic `payment-events` + 3 subs | SNS topic + 3 SQS queues |
| Topic `shipping-events` + 2 subs | SNS topic + 2 SQS queues |
| Queue `send-notification` | SQS queue `send-notification` (no SNS needed; direct send) |

Idempotency, dead-letter handling, and the transactional outbox all behave the same — Wolverine implements them at the framework level.

### What changes during the swap

- **`AppHost.cs`** — In production, infrastructure usually lives outside Aspire (Terraform/CDK/CloudFormation). The Aspire-driven `AddAzureServiceBus(...).RunAsEmulator()` exists only for local dev.
- **Each service's `Program.cs`** — `opts.UseAzureServiceBus(...)` becomes `opts.UseAmazonSqs(...)` (and SNS publishing config). The package reference swap is `WolverineFx.AzureServiceBus` → `WolverineFx.AmazonSqs`.
- **Handlers and domain code** — zero changes. They read/write the same `Envelope.Headers`, the same `IMessageBus.PublishAsync(...)`. The `WolverineEventPublisher` adapter we already have is the seam.
- **Database hosting** — Postgres → Amazon RDS for PostgreSQL or Aurora; SQL Server → RDS for SQL Server. EF Core providers stay the same.
- **Identity** — Keycloak can run on ECS/EKS/EC2, or swap for **Amazon Cognito**. JWT validation in `ServiceDefaults` doesn't care which IdP issued the token, only that audience/issuer/signing match.
- **Telemetry** — OpenTelemetry already exports OTLP; point at any OTel-compatible AWS collector (X-Ray via OTel exporter, Managed Prometheus + Managed Grafana, or third-party APM).

### What stays the same

The entire Domain layer, the entire Application layer (handlers + commands + queries), all middleware, the API versioning scheme, the auth/JWT flow, the saga orchestration, the transactional outbox, the optimistic concurrency tokens. The cloud-portability story is real because we kept Wolverine + EF Core abstractions clean and put cloud-specific calls only in `AppHost.cs` and per-service `Program.cs` configuration blocks.

### Open work

A non-trivial AWS deploy still needs (separate from this codebase): Terraform/CDK to provision SNS topics, SQS queues, RDS instances, ECS/EKS clusters; a CI/CD pipeline; secret management (AWS Secrets Manager); and the production migration deployment step listed above. None of these are blocked by application-level changes.
