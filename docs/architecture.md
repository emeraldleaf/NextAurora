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
                         |  (Blazor WASM)    |     | (Blazor Server)   |
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
                    |     Azure Service Bus       |
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

## Service Architecture

Each backend service follows a **Clean Architecture** layered structure:

```
ServiceName/
  ServiceName.Domain/          # Entities, enums, repository interfaces
  ServiceName.Application/     # Commands, queries, handlers (MediatR)
  ServiceName.Infrastructure/  # EF Core, repositories, messaging, external gateways
  ServiceName.Api/             # ASP.NET Core host, endpoints, DI composition
```

### Layer Responsibilities

| Layer | Responsibility | Dependencies |
|-------|---------------|-------------|
| **Domain** | Entities, value objects, domain interfaces, business rules | None |
| **Application** | CQRS commands/queries, MediatR handlers, application interfaces | Domain |
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

Used for frontend-to-service communication. ASP.NET Core Minimal APIs with OpenAPI documentation.

**When to use:** Client-facing endpoints accessed by Storefront and SellerPortal.

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
| **EventLogs** | Id, EventType, Payload, CorrelationId, EntityId, CreatedAt, PublishedAt (null = unpublished) |

#### payments-db (SQL Server)

| Table | Columns |
|-------|---------|
| **Payments** | Id, OrderId, Amount, Currency, Status, Provider, ExternalTransactionId, CreatedAt, CompletedAt, FailureReason |
| **Refunds** | Id, PaymentId, Amount, Reason, Status, CreatedAt |
| **EventLogs** | Id, EventType, Payload, CorrelationId, EntityId, CreatedAt, PublishedAt (null = unpublished) |

#### shipping-db (PostgreSQL)

| Table | Columns |
|-------|---------|
| **Shipments** | Id, OrderId, Carrier, TrackingNumber, Status, CreatedAt, DispatchedAt, DeliveredAt |
| **TrackingEvents** | Id, ShipmentId (FK), Description, Status, OccurredAt |
| **EventLogs** | Id, EventType, Payload, CorrelationId, EntityId, CreatedAt, PublishedAt (null = unpublished) |

---

## Event-Driven Architecture

### Message Topology

```
Azure Service Bus
  |
  +-- Topic: order-events
  |     +-- Subscription: payment-sub  -> PaymentService
  |     +-- Subscription: notify-sub   -> NotificationService
  |
  +-- Topic: payment-events
  |     +-- Subscription: order-sub    -> OrderService
  |     +-- Subscription: shipping-sub -> ShippingService
  |     +-- Subscription: notify-sub   -> NotificationService
  |
  +-- Topic: shipping-events
  |     +-- Subscription: order-sub    -> OrderService
  |     +-- Subscription: notify-sub   -> NotificationService
  |
  +-- Queue: send-notification         -> NotificationService
```

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
- Service: `CatalogGrpcService` wraps existing MediatR handlers
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
- **MediatR Pipeline Logging:** `LoggingBehavior<TRequest,TResponse>` (registered in each service after `ValidationBehavior`) times every command/query handler and logs start, elapsed time, and outcome.
- **Metrics:** Business counters via `Meter("NextAurora")` in `NovaCraftMetrics`: `orders.placed`, `payments.processed` (tag: `outcome`), `shipments.dispatched`, `notifications.sent` (tag: `channel`), `messages.abandoned` (tags: `subject`, `service`). Exported via OTLP; visible in Aspire Metrics dashboard.
- **Logging:** Structured logging with OpenTelemetry export
- **Dashboard:** Aspire dashboard shows all services, traces, logs, and metrics in development

### Resilience
- Standard resilience handler on all HTTP clients (retries, circuit breaker, timeout, rate limiting)
- gRPC calls benefit from HTTP client resilience via service discovery

### Input Validation
- **FluentValidation:** All commands have corresponding validator classes (e.g., `CreateProductCommandValidator`, `PlaceOrderCommandValidator`, `ProcessPaymentCommandValidator`)
- **MediatR Pipeline Behavior:** `ValidationBehavior<TRequest, TResponse>` runs validators before handlers, throwing `ValidationException` with structured errors on failure
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

## Design Patterns

| Pattern | Implementation |
|---------|---------------|
| **CQRS** | Separate command and query objects via MediatR |
| **Repository** | EF Core repositories behind domain interfaces |
| **Domain-Driven Design** | Aggregates with factory methods, guard clauses, encapsulated collections (`IReadOnlyList`), no public setters |
| **Validation Pipeline** | FluentValidation + MediatR `IPipelineBehavior` for pre-handler validation |
| **Event-Driven Architecture** | Azure Service Bus pub/sub with topic/subscription model |
| **Choreography Saga** | Order lifecycle managed through event chain across services |
| **Anti-Corruption Layer** | StripePaymentGateway isolates domain from external payment API |
| **Service Discovery** | Aspire-based automatic service resolution |
| **Strangler Fig (Ready)** | REST + gRPC endpoints allow incremental migration |

---

## Future Considerations

### Implemented
- **Input Validation** - FluentValidation on all commands with MediatR pipeline behavior
- **MediatR Pipeline Logging** - `LoggingBehavior` times every handler; logs correlation ID, elapsed time, and outcome
- **Context Propagation** - `CorrelationId`, `UserId`, `SessionId` flow through HTTP and Service Bus; see [docs/context-propagation.md](context-propagation.md)
- **Domain Invariants** - Guard clauses in all entity factory methods
- **Global Exception Handling** - ProblemDetails responses, no internal state leakage
- **Encapsulated Aggregates** - `IReadOnlyList` collections, private backing fields
- **HTTPS Redirection** - Enforced in production
- **Idempotent Event Handling** - Status guards in all event handlers; GetByOrderId checks prevent duplicate processing
- **Dead Letter Queue Processing** - `messages.abandoned` metric counter on all processors; admin replay endpoints at `/admin/events/replay/{id}`

### Not Yet Implemented
- **Authentication & Authorization** - JWT/OAuth2 for API security
- **API Gateway** - Centralized routing, rate limiting, auth
- **Saga Compensation** - Rollback logic for failed payments/shipments
- **Distributed Caching** - Redis caching strategy for CatalogService
- **Frontend Implementation** - Storefront and SellerPortal business logic
- **Database Migrations** - EF Core migration pipeline
- **Integration Tests** - Testcontainers-based service testing
- **Order Cancellation Flow** - Cancel event and compensation logic
