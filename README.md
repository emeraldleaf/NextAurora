# NextAurora

A microservices-based e-commerce platform built with .NET 10, Blazor, and .NET Aspire.

NextAurora demonstrates a production-style distributed system with event-driven architecture, CQRS, domain-driven design, and gRPC for inter-service communication.

## Architecture Overview

```
+-----------------------------------------------------+
|                   FRONTEND LAYER                     |
|                                                      |
|   Storefront            SellerPortal                 |
|   (Blazor WASM)         (Blazor Server)              |
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
|   Azure Service Bus                                  |
|                                                      |
|   Topics:                                            |
|   order-events -----> PaymentSvc, NotificationSvc    |
|   payment-events ---> OrderSvc, ShippingSvc          |
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
| **Storefront** | - | Customer-facing Blazor WASM SPA |
| **SellerPortal** | - | Merchant dashboard for product/order management |

## Tech Stack

- **.NET 10** / C# 13
- **ASP.NET Core** Minimal APIs
- **Blazor WebAssembly** (Storefront) and **Blazor Server** (SellerPortal)
- **Entity Framework Core 10** (PostgreSQL + SQL Server)
- **Azure Service Bus** for async event-driven messaging
- **gRPC** for synchronous inter-service communication
- **MediatR** for CQRS command/query dispatch
- **.NET Aspire** for orchestration, service discovery, and observability
- **OpenTelemetry** for distributed tracing, metrics, and logging
- **Redis** for caching

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Aspire CLI](https://learn.microsoft.com/en-us/dotnet/aspire/)

```bash
dotnet tool install --global aspire.cli
```

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

### Catalog Service
- `GET /api/products` - List all products
- `GET /api/products/{id}` - Get product by ID
- `GET /api/products/search?query=` - Search products
- `POST /api/products` - Create a product
- `PUT /api/products/{id}` - Update a product

### Order Service
- `POST /api/orders` - Place an order
- `GET /api/orders/{id}` - Get order by ID
- `GET /api/orders/buyer/{buyerId}` - Get orders by buyer

### Payment Service
- `POST /api/payments/process` - Process a payment

### Shipping Service
- `GET /api/shipments/order/{orderId}` - Get shipment by order ID

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
  Storefront/                 # Blazor WASM customer app
  SellerPortal/               # Blazor Server merchant dashboard
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
- **`CorrelationIdMiddleware`** (`ServiceDefaults`) — HTTP entry point; extracts all three IDs
- **`LoggingBehavior`** — MediatR pipeline behavior; opens a logger scope so handler log lines inherit the IDs automatically
- **`ServiceBusEventPublisher`** — writes IDs into outgoing message `ApplicationProperties`
- **Service Bus processors** — read IDs from incoming messages and restore them into the logging scope

Order, Payment, and Shipping services also persist every published event to an `EventLogs` table, enabling replay of historical events for debugging. Admin endpoints (protected by `X-Admin-Key`) are available at `/admin/events`.

See [`docs/context-propagation.md`](docs/context-propagation.md) and [`docs/event-replay.md`](docs/event-replay.md) for full details.

## Code Quality

The project enforces code quality standards from day one:

- **Directory.Build.props** - Centralized build settings, `TreatWarningsAsErrors`, static analyzers (Meziantou, SonarAnalyzer, Roslynator)
- **Directory.Packages.props** - Central Package Management for consistent NuGet versions
- **.editorconfig** - Coding standards and naming conventions
- **GitHub Actions** - CI pipeline for build and test on every push/PR

## Security & Validation

- **Input Validation** - FluentValidation validators on all commands, enforced via MediatR pipeline behavior
- **Domain Invariants** - All entities enforce business rules in factory methods (guard clauses for invalid state)
- **Global Exception Handling** - ProblemDetails responses with trace IDs; internal details never leaked to clients
- **Encapsulated Aggregates** - Collections exposed as `IReadOnlyList<T>` with private backing fields
- **HTTPS Redirection** - Enforced in production environments
- **Server-Side Pricing** - Order totals calculated from catalog data, not client-submitted prices

## Communication Patterns

| Pattern | Technology | Use Case |
|---------|-----------|----------|
| **Event-Driven (Async)** | Azure Service Bus | Order workflows, payment processing, shipping, notifications |
| **gRPC (Sync)** | Protocol Buffers | Product validation during order placement |
| **REST (External)** | ASP.NET Core Minimal APIs | Frontend-to-service communication |

## License

This project is for educational and demonstration purposes.
