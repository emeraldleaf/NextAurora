# NextAurora

A microservices-based e-commerce platform built with .NET 10, Blazor, and .NET Aspire.

NextAurora demonstrates a production-style distributed system with event-driven architecture, CQRS, domain-driven design, and gRPC for inter-service communication.

> **Want the full picture in one view?** See [docs/nextaurora-architecture.excalidraw](docs/nextaurora-architecture.excalidraw) — services, Service Bus topology, databases, and the 10-step order-placement saga in a single diagram. Open in VSCode (Excalidraw plugin) or drag onto [excalidraw.com](https://excalidraw.com).

## Architecture Overview

```
+-----------------------------------------------------+
|                   FRONTEND LAYER                     |
|                                                      |
|   Storefront            SellerPortal                 |
|   (Blazor WASM)         (scaffold)              |
+-------+--------------------+-------------------------+
        |  REST              |  REST
        v                    v
+-----------------------------------------------------+
|                    API LAYER                          |
|                                                      |
|  +--------------+    +---------------+               |
|  | CatalogSvc   |<---| OrderSvc      |               |
|  | (PostgreSQL)  |gRPC| (SQL Server)  |               |
|  +--------------+    +------+--------+               |
|                             |                        |
|  +--------------+    +------+--------+    +--------+ |
|  | PaymentSvc   |    | ShippingSvc   |    |Notif-  | |
|  | (SQL Server)  |    | (PostgreSQL)  |    |ication | |
|  +--------------+    +---------------+    |Svc     | |
|                                           +--------+ |
+-----------------------------------------------------+
        |                |               |
        v                v               v
+-----------------------------------------------------+
|                 MESSAGING LAYER                      |
|                                                      |
|   Async Messaging (Topics & Subscriptions)          |
|   Local/CI: Azure Service Bus emulator              |
|   AWS prod: Amazon SNS + SQS                        |
|                                                      |
|   Topics:                                            |
|   order-events -----> PaymentSvc, NotificationSvc    |
|   payment-events ---> OrderSvc, ShippingSvc,         |
|                       NotificationSvc                |
|   shipping-events --> OrderSvc, NotificationSvc      |
|                                                      |
|   Queue:                                             |
|   send-notification -> NotificationSvc               |
+-----------------------------------------------------+
        |                |               |
        v                v               v
+-----------------------------------------------------+
|                INFRASTRUCTURE LAYER                  |
|                                                      |
|  PostgreSQL    SQL Server    Redis    App Insights    |
|  (catalog,     (orders,     (cache)  (telemetry)     |
|   shipping)     payments)                            |
+-----------------------------------------------------+

Orchestrated by .NET Aspire (service discovery, health checks, OpenTelemetry)
```

## Services

| Service | Database | Purpose |
|---------|----------|---------|
| **CatalogService** | PostgreSQL | Product catalog, categories, search |
| **OrderService** | SQL Server | Order placement, lifecycle management |
| **PaymentService** | SQL Server | Payment processing (Stripe integration) |
| **ShippingService** | PostgreSQL | Shipment creation, tracking |
| **NotificationService** | Stateless | Email notifications (order confirmations, shipping updates) |
| **Storefront** | - | Customer-facing Blazor WASM SPA (scaffold) |
| **SellerPortal** | - | Merchant dashboard (scaffold — currently a static-file ASP.NET Core host, no UI framework chosen) |

## Tech Stack

- **.NET 10** / C# 13
- **ASP.NET Core** Minimal APIs
- **Blazor WebAssembly** (Storefront, scaffolded — no business logic yet)
- **ASP.NET Core static-file host** (SellerPortal scaffold — no UI framework chosen yet, currently serves a placeholder `index.html`)
- **Entity Framework Core 10** (PostgreSQL + SQL Server) with EF migrations
- **Azure Service Bus** for async event-driven messaging
- **Wolverine** for command/query dispatch, message handling, and the transactional outbox
- **gRPC** for synchronous inter-service communication
- **Keycloak + JWT Bearer** for authentication and authorization
- **Asp.Versioning** for URL-segment API versioning (`/api/v1/...`)
- **.NET Aspire** for orchestration, service discovery, and observability
- **OpenTelemetry** for distributed tracing, metrics, and logging
- **HybridCache** (.NET 10) for two-tier read caching — in-process MemoryCache (L1) + **Redis** (L2), with stampede protection and tag-based invalidation

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) — running, not just installed (Aspire spins up Postgres/SQL Server/Service Bus emulator/Keycloak/Redis as containers)
- [Aspire CLI](https://learn.microsoft.com/en-us/dotnet/aspire/)
- **ASP.NET Core dev certificate** — required by the Aspire dashboard's HTTPS endpoint. One-time per machine:

```bash
dotnet tool install --global aspire.cli
dotnet dev-certs https
dotnet dev-certs https --trust   # prompts for keychain password on macOS
```

> **Gotcha:** if `dotnet dev-certs` prints *"HTTPS development certificate operations are disabled in this environment. Your application will run on HTTP for local development."*, the env var `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` is set somewhere in your shell init (commonly `~/.zshrc`). Find and remove it: `grep -rn "DOTNET_GENERATE_ASPNET_CERTIFICATE" ~/.zshrc ~/.zprofile ~/.zshenv ~/.bashrc ~/.bash_profile ~/.profile`, delete the matching line, open a fresh terminal, then re-run the dev-certs commands.

## Getting Started

1. **Clone the repository**

```bash
git clone <repo-url>
cd NextAurora
```

2. **Restore dependencies**

```bash
dotnet restore
```

3. **Run with Aspire**

```bash
dotnet run --project NextAurora.AppHost
```

This starts all services, databases (PostgreSQL, SQL Server), Redis, and Azure Service Bus emulator in Docker containers. The Aspire dashboard opens automatically showing all services, health status, logs, and distributed traces.

4. **Access the applications**

| Application | URL |
|-------------|-----|
| Aspire Dashboard | https://localhost:17225 |
| Storefront | Shown in Aspire Dashboard |
| SellerPortal | Shown in Aspire Dashboard |
| CatalogService API | Shown in Aspire Dashboard |
| OrderService API | Shown in Aspire Dashboard |

## API Endpoints

🔒 = requires JWT Bearer authentication. Pagination params apply to list endpoints (`?page=1&pageSize=50`, server cap 100).

**API versioning:** URL-segment versioning via `Asp.Versioning.Http`. The version is required in the route — `/api/v1/...`. Default version is `1.0`; unversioned URLs (`/api/products`) return 400. Versioned endpoints appear in OpenAPI under group `v1`.

### Catalog Service
- `GET /api/v1/products?page=&pageSize=` - List products (paginated)
- `GET /api/v1/products/{id}` - Get product by ID
- `GET /api/v1/products/search?query=&page=&pageSize=` - Search products (rate-limited, paginated)
- 🔒 `POST /api/v1/products` - Create a product
- 🔒 `PUT /api/v1/products/{id}` - Update a product

### Order Service (entire group 🔒)
- 🔒 `POST /api/v1/orders` - Place an order (buyerId in command must match JWT `sub`)
- 🔒 `GET /api/v1/orders/{id}` - Get order by ID
- 🔒 `GET /api/v1/orders/buyer/{buyerId}?page=&pageSize=` - Get orders by buyer (paginated; route buyerId must match JWT `sub`)

### Payment Service
- 🔒 `POST /api/v1/payments/process` - Process a payment (rate-limited)

### Shipping Service (entire group 🔒)
- 🔒 `GET /api/v1/shipments/order/{orderId}` - Get shipment by order ID

## Project Structure

```
NextAurora/
  NextAurora.AppHost/          # Aspire orchestrator
  NextAurora.ServiceDefaults/  # Shared OpenTelemetry, health checks, resilience
  NextAurora.Contracts/        # Shared events, commands, DTOs
  CatalogService/
    CatalogService.Domain/        # Entities, interfaces
    CatalogService.Application/   # CQRS handlers
    CatalogService.Infrastructure/# EF Core, repositories
    CatalogService.Api/           # Endpoints, gRPC server
  OrderService/
    OrderService.Domain/
    OrderService.Application/
    OrderService.Infrastructure/
    OrderService.Api/             # Endpoints, gRPC client
  PaymentService/
    PaymentService.Domain/
    PaymentService.Application/
    PaymentService.Infrastructure/
    PaymentService.Api/
  ShippingService/
    ShippingService.Domain/
    ShippingService.Application/
    ShippingService.Infrastructure/
    ShippingService.Api/
  NotificationService/
    NotificationService.Domain/
    NotificationService.Application/
    NotificationService.Infrastructure/
    NotificationService.Api/
  Storefront/                 # Blazor WASM customer app (scaffold)
  SellerPortal/               # ASP.NET Core static-file host scaffold (UI framework TBD)
```

## Event Flow

The order lifecycle is fully automated through event-driven choreography:

```
Customer places order
  -> OrderService creates order (validates products via gRPC)
  -> Publishes OrderPlacedEvent

PaymentService receives OrderPlacedEvent
  -> Processes payment via Stripe gateway
  -> Publishes PaymentCompletedEvent (or PaymentFailedEvent)

OrderService receives PaymentCompletedEvent
  -> Marks order as Paid

ShippingService receives PaymentCompletedEvent
  -> Creates shipment, assigns carrier and tracking number
  -> Publishes ShipmentDispatchedEvent

OrderService receives ShipmentDispatchedEvent
  -> Marks order as Shipped

NotificationService receives OrderPlacedEvent
  -> Sends "Order Received" notification

NotificationService receives ShipmentDispatchedEvent
  -> Sends "Order Shipped" notification with tracking info
```

## Observability

Every request and Service Bus message carries three identifiers through the entire chain:

| Field | Source | Propagated Via |
|-------|--------|---------------|
| `CorrelationId` | `X-Correlation-Id` header (generated if absent) | Activity baggage → Service Bus `ApplicationProperties` |
| `UserId` | JWT `sub` claim | Activity baggage → Service Bus `ApplicationProperties` |
| `SessionId` | `X-Session-Id` header | Activity baggage → Service Bus `ApplicationProperties` |

These appear on **every structured log line** in every service, making it possible to search for a single `CorrelationId` and see the complete transaction timeline across all five services.

Key components:
- **`CorrelationIdMiddleware`** (`ServiceDefaults`) — HTTP entry point; extracts all three IDs from request headers and JWT claims
- **`ContextPropagationMiddleware`** — Wolverine middleware on the incoming side; restores the three IDs from envelope headers into the logger scope before each handler runs
- **`OutgoingContextMiddleware`** — Wolverine middleware on the outgoing side; stamps the three IDs onto outgoing message envelopes
- **`WolverineEventPublisher`** — thin pass-through to `IMessageBus.PublishAsync` so domain code stays infrastructure-agnostic

Order, Payment, and Shipping run **Wolverine's transactional outbox**: outgoing events persist to a `wolverine` schema in each service's database in the same DB transaction as the entity write, then dispatch to Service Bus via a background flush. See [`docs/context-propagation.md`](docs/context-propagation.md) and [`docs/performance-and-data-correctness.md`](docs/performance-and-data-correctness.md) for full details.

## Performance Testing

Two harnesses, both opt-in.

### Code-level micro-benchmarks (BenchmarkDotNet)

```bash
dotnet run -c Release --project benchmarks/NextAurora.Benchmarks
# Or filter to a single benchmark class:
dotnet run -c Release --project benchmarks/NextAurora.Benchmarks -- --filter '*OrderFactory*'
```

Always run in **Release** — Debug numbers are not representative. Currently includes `OrderFactoryBenchmarks` (Order aggregate creation with 1/5/25 line counts). Add new benchmarks under `benchmarks/NextAurora.Benchmarks/` following the same pattern.

### Endpoint load tests (k6)

```bash
brew install k6   # macOS; see https://k6.io/docs/getting-started/installation/ for others
# AppHost must be running. CATALOG_URL is the same value smoke-test.sh uses.
CATALOG_URL=https://localhost:XXXXX k6 run scripts/k6/smoke.js
```

Currently includes `smoke.js` (1 VU for 30s with p95 < 500ms / error rate < 1% thresholds). See [scripts/k6/README.md](scripts/k6/README.md).

## Code Quality

The project enforces code quality standards from day one:

- **Directory.Build.props** - Centralized build settings, `TreatWarningsAsErrors`, static analyzers (Meziantou, SonarAnalyzer, Roslynator)
- **Directory.Packages.props** - Central Package Management for consistent NuGet versions
- **.editorconfig** - Coding standards and naming conventions
- **GitHub Actions** - CI pipeline for build and test on every push/PR

## Authentication

JWT Bearer authentication is configured in `NextAurora.ServiceDefaults` and applied to every service. Identity is provided by **Keycloak**, which runs as an Aspire-managed container with the `nextaurora-realm` imported from `realms/nextaurora-realm.json`.

- **JWT validation** — issuer, audience, lifetime, and signing all validated via `AddJwtBearer` (`Authentication:Authority` config falls back to `Keycloak:Url`).
- **Claim mapping** — `preferred_username` is the name claim, `realm_access.roles` is the role claim.
- **Endpoint protection** — `.RequireAuthorization()` on every state-changing endpoint and the buyer-scoped read endpoints (Catalog write endpoints, all of `/api/v1/orders`, `/api/v1/payments/process`, all of `/api/v1/shipments`). Public read endpoints (`GET /api/v1/products`) remain anonymous.
- **Buyer-scope enforcement** — endpoints that operate on a buyer's data (e.g. `GET /api/v1/orders/buyer/{buyerId}`, `POST /api/v1/orders`) verify the JWT `sub` claim matches the route/body buyer ID and return 403 otherwise.

If Keycloak isn't configured (no `Authentication:Authority` and no `Keycloak:Url`), ServiceDefaults registers no-op auth services — `UseAuthentication` doesn't crash, but every `.RequireAuthorization()`-protected endpoint returns 401.

## Security & Validation

- **Input Validation** - FluentValidation validators on all commands, enforced via Wolverine's `UseFluentValidation()` pipeline policy (validators run before handlers; failures throw `ValidationException`)
- **Domain Invariants** - All entities enforce business rules in factory methods (guard clauses for invalid state)
- **Optimistic Concurrency** - `xmin` (Postgres) / `RowVersion` (SQL Server) tokens on every aggregate; `DbUpdateConcurrencyException` becomes 409 Conflict on the HTTP path and triggers Wolverine retry on the message path
- **Transactional Outbox** - Wolverine outbox in Order, Payment, Shipping; entity writes and event publishes commit atomically
- **Rate Limiting** - Fixed-window limiters on `/api/v1/products/search` (search) and `/api/v1/payments/process` (payments)
- **Global Exception Handling** - ProblemDetails responses with trace IDs; internal details never leaked to clients
- **Encapsulated Aggregates** - Collections exposed as `IReadOnlyList<T>` with private backing fields
- **HTTPS Redirection** - Enforced in production environments
- **Server-Side Pricing** - Order totals calculated from catalog data, not client-submitted prices
- **Pagination Caps** - List endpoints take `page`/`pageSize` (default 50, server-side max 100)

## Communication Patterns

| Pattern | Technology | Use Case |
|---------|-----------|----------|
| **Event-Driven (Async)** | Azure Service Bus | Order workflows, payment processing, shipping, notifications |
| **gRPC (Sync)** | Protocol Buffers | Product validation during order placement |
| **REST (External)** | ASP.NET Core Minimal APIs | Frontend-to-service communication |

## Documentation

| Guide | Description |
|-------|-------------|
| [How It Works](docs/how-it-works.md) | Developer walkthrough — Clean Architecture, CQRS via Wolverine, request lifecycle, outbox, event flow, testing |
| [Architecture](docs/architecture.md) | Service diagrams, communication matrix, domain model, design patterns |
| [Performance & Data Correctness](docs/performance-and-data-correctness.md) | Hard rules + decisions: AsNoTracking strategy, optimistic concurrency tokens, Wolverine outbox, HybridCache, Dapper escape hatch |
| [Observability](docs/observability.md) | Correlation/user/session ID propagation, distributed tracing, Wolverine handler logging, DLQ handling, metrics |
| [Event Replay](docs/event-replay.md) | Wolverine outbox state, where to inspect outgoing/dead-letter envelopes, `IMessageStore` API |
| [Business Requirements](docs/BRD.md) | Functional requirements, implementation status, business processes, glossary |
| [Project Status](docs/STATUS.md) | Cross-session entry point — recently landed, next, open issues |

## License

[MIT](LICENSE) — Copyright (c) 2026 Joshua Dell. Free to use, modify, and redistribute with attribution.
