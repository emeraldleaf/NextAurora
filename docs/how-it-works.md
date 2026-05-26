# How NextAurora Works — Developer Guide

This guide explains how the code is organized, how requests flow through the system, and how the services communicate. It is intended for developers who want to understand the codebase quickly.

---

## Table of Contents

1. [Project Layout](#1-project-layout)
2. [Per-Service Architecture: Clean Architecture or VSA](#2-per-service-architecture-clean-architecture-or-vsa)
3. [Domain Model — Rich Entities with Guard Clauses](#3-domain-model--rich-entities-with-guard-clauses)
4. [CQRS + Wolverine — The Request Pipeline](#4-cqrs--wolverine--the-request-pipeline)
5. [A Complete Request: Placing an Order](#5-a-complete-request-placing-an-order)
6. [Service-to-Service Communication](#6-service-to-service-communication)
7. [Event-Driven Workflow](#7-event-driven-workflow)
8. [Cross-Cutting Concerns](#8-cross-cutting-concerns)
9. [Infrastructure and Local Development (Aspire)](#9-infrastructure-and-local-development-aspire)
10. [Testing Strategy](#10-testing-strategy)
11. [Where to Look for What](#11-where-to-look-for-what)

---

## 1. Project Layout

The solution is organized into five backend microservices, two frontend applications, and three shared projects.

```
NextAurora/
  NextAurora.AppHost/          # Aspire orchestrator — starts everything locally
  NextAurora.ServiceDefaults/  # Shared middleware, telemetry, exception handling
  NextAurora.Contracts/        # Shared event classes and DTOs (cross-service contracts)

  CatalogService/               # Clean Architecture (largest service)
    CatalogService.Domain/         # Product, Category entities; repository interfaces
    CatalogService.Application/    # Commands, queries, Wolverine handlers, validators
    CatalogService.Infrastructure/ # EF Core (PostgreSQL), repositories, HybridCache
    CatalogService.Api/            # ASP.NET Core host, REST endpoints, gRPC server

  OrderService/                 # Vertical Slice Architecture (single project, SQL Server)
    Features/                      # PlaceOrder.cs, GetOrderById.cs, saga handlers
    Domain/                        # Order aggregate, ports
    Infrastructure/                # EF Core, repositories, gRPC client to Catalog
    Endpoints/                     # Minimal-API HTTP surface
  PaymentService/               # VSA (SQL Server)
  ShippingService/              # VSA (PostgreSQL)
  NotificationService/          # VSA (stateless, no database)

  Storefront/        # Blazor WASM — customer-facing SPA (scaffold only)
  SellerPortal/      # static-file host scaffold (UI framework not yet chosen)

  tests/
    OrderService.Tests.Unit
    CatalogService.Tests.Unit
    PaymentService.Tests.Unit
    ShippingService.Tests.Unit
    NotificationService.Tests.Unit
```

---

## 2. Per-Service Architecture: Clean Architecture or VSA

NextAurora uses **two architectural shapes side-by-side**, calibrated to each service's
complexity. The cross-service diff is intentional, not an inconsistency to clean up.

### CatalogService — Clean Architecture (4 projects)

The largest service uses the classic four-project split with the dependency rule enforced by
project references at compile time:

```
Domain          →  no dependencies
Application     →  Domain only
Infrastructure  →  Domain + Application
Api             →  all layers (DI composition root)
```

| Project | What lives there |
|-------|----------------|
| **Domain** | `Product`, `Category` entities; `IProductRepository` interface; domain types only — zero framework dependencies |
| **Application** | `GetProductByIdQuery` + handler, `CreateProductCommand` + validator + handler, `ProductMapper`, `IProductCache` port |
| **Infrastructure** | `CatalogDbContext`, `ProductRepository`, `HybridProductCache` (the L1+L2 implementation), DI registration |
| **Api** | `Program.cs`, REST endpoints (`CatalogEndpoints`), gRPC server (`CatalogGrpcService`), rate limiter wiring |

### Order / Payment / Shipping / Notification — Vertical Slice Architecture (1 project each)

Smaller services collapsed to one csproj with code organized by *feature* instead of *layer*:

```
ServiceName/
  Features/          # Per use case: command/query record + validator + handler co-located.
                    # Saga event handlers live here too (they own real state machines).
  Domain/            # Aggregate roots, value objects, ports (IFooRepository, IEventPublisher).
  Infrastructure/    # EF Core (Data/ + Migrations/), repositories, gateways, DI composition.
  Endpoints/         # Minimal-API HTTP surface.
  Program.cs         # Composition root.
```

The Domain folder is just a folder (no build-time boundary). Discipline does the work
compile-time project references used to. **`IFooRepository` and `IEventPublisher` ports stay**
in both shapes — they're earning their keep through unit-test substitution, not the project
boundary.

See [CLAUDE.md "Project Structure"](../CLAUDE.md#project-structure) for the decision rule.

---

## 3. Domain Model — Rich Entities with Guard Clauses

All domain entities follow a consistent pattern:

- **Private constructor** — prevents construction without validation
- **Static `Create()` factory** — validates invariants before returning an entity
- **Private state, domain methods** — state changes happen through explicit methods, not property setters
- **Encapsulated collections** — child collections are exposed as `IReadOnlyList<T>` backed by a private `List<T>`

### Example: `Order`

```csharp
public class Order
{
    public Guid Id { get; private set; }
    public OrderStatus Status { get; private set; }

    private readonly List<OrderLine> _lines = [];
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    private Order() { }   // EF Core uses this

    public static Order Create(Guid buyerId, string currency, List<OrderLine> lines)
    {
        if (buyerId == Guid.Empty)
            throw new ArgumentException("Buyer ID must not be empty.", nameof(buyerId));
        if (lines.Count == 0)
            throw new ArgumentException("Order must contain at least one line.", nameof(lines));
        // ... more guards
        var order = new Order { Id = Guid.NewGuid(), Status = OrderStatus.Placed, ... };
        order._lines.AddRange(lines);
        return order;
    }

    public void MarkAsPaid()
    {
        if (Status != OrderStatus.Placed)
            throw new InvalidOperationException("Cannot mark order as paid in the current status.");
        Status = OrderStatus.Paid;
        PaidAt = DateTime.UtcNow;
    }
}
```

**What this means in practice:**
- You cannot create an invalid `Order` — `Create()` throws before returning.
- You cannot set `order.Status = OrderStatus.Paid` directly — there is no public setter.
- You cannot pay an already-paid order — `MarkAsPaid()` checks the current status.
- Business rules live in the domain, not scattered across handlers or controllers.

---

## 4. CQRS + Wolverine — The Request Pipeline

All business operations are expressed as either a **Command** (changes state, returns an ID or nothing) or a **Query** (reads data, returns a DTO). [Wolverine](https://wolverinefx.net/) discovers handlers by convention and dispatches messages to them — no `IRequestHandler<T>` interface, no `MediatR`, no per-call registration.

### Command Example

```csharp
// Application/Commands/PlaceOrderCommand.cs
public record PlaceOrderCommand(Guid BuyerId, string Currency, List<OrderLineItem> Lines);

// Features/PlaceOrder.cs
public class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.BuyerId).NotEmpty();
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Lines).NotEmpty();
    }
}

// Features/PlaceOrder.cs
// No interface to implement. Wolverine discovers public classes whose
// public *Async method matches a known message type as the parameter.
public class PlaceOrderHandler(IOrderRepository repo, ICatalogClient catalog, /* ... */)
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken ct)
    {
        // 1. Validate products via gRPC
        // 2. Reserve stock
        // 3. Create Order aggregate
        // 4. Persist to database (Wolverine's AutoApplyTransactions wraps this)
        // 5. Stage OrderPlacedEvent into the outbox via cascading return
        return order.Id;
    }
}
```

Conventions:
- Handlers live in `*.Application/Handlers/`, named `*Handler` with a public `HandleAsync` method.
- The first parameter is the message; subsequent parameters are dependencies injected by Wolverine from the DI container.
- For commands that return a value, `HandleAsync` returns it directly; for events, it returns nothing (or returns a *cascading* event that Wolverine publishes after the handler).
- Tests instantiate the handler with mocks (NSubstitute) and call `HandleAsync` directly — no Wolverine bus needed in unit tests.

### The Wolverine pipeline (runs around every handler)

```
HTTP endpoint (Minimal API)
  → bus.InvokeAsync(command)             // Wolverine entry point
    → FluentValidation policy            // rejects invalid input with 400
    → ContextPropagationMiddleware       // restores correlation/user/session, opens logger scope
    → AutoApplyTransactions              // begins EF transaction for handlers that touch a DbContext
    → Handler (HandleAsync)
    → Cascading messages staged into outbox (same DB transaction)
    → SaveChanges + commit               // entity write + outbox row commit atomically
  → response
```

Each piece is wired in the service's `Program.cs` via `builder.Host.UseWolverine(opts => { ... })`:

```csharp
opts.Discovery.IncludeAssembly(typeof(PlaceOrderCommand).Assembly);
opts.UseFluentValidation();                                    // validation policy
opts.AddNextAuroraContextPropagation();                         // correlation/user/session
opts.PersistMessagesWithSqlServer(connStr, "wolverine");       // (or Postgresql) — outbox storage
opts.UseEntityFrameworkCoreTransactions();                      // EF integration
opts.Policies.AutoApplyTransactions();                          // wrap handlers in a tx
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();          // outgoing messages → outbox
opts.Policies.LogMessageStarting(LogLevel.Information);         // handler logs
opts.AddConcurrencyRetry();                                     // retry DbUpdateConcurrencyException
```

Tier-1 detail: validation runs *before* `ContextPropagationMiddleware`, so 400s for invalid commands don't open a logger scope (and don't add noise to the trace). The handler only ever sees valid messages with a correlation ID already restored from the inbound transport.

### Two containers, not one — Wolverine's handler map vs. `IServiceCollection`

`opts.Discovery.IncludeAssembly(typeof(PlaceOrderCommand).Assembly)` builds Wolverine's *own* internal lookup table — a `Dictionary<MessageType, HandlerType>` that `IMessageBus` consults to decide which class to instantiate. Wolverine then constructs the handler itself via `IServiceScopeFactory` (one fresh scope per message), injects its constructor dependencies from the scope, and invokes `HandleAsync`. **The handler type itself is never registered in `IServiceCollection`.**

That's fine for production code because everything goes through `IMessageBus`:

```csharp
orders.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
    await bus.InvokeAsync<OrderSummaryDto?>(new GetOrderByIdQuery(id), ct));
```

But it breaks for any code path that resolves a handler directly. The canonical example is **read-handler integration tests** — these resolve the handler concretely to assert the EF projection SQL without booting auth + HTTP:

```csharp
await using var scope = _factory.CreateDbScope();
var handler = scope.ServiceProvider.GetRequiredService<GetOrderByIdHandler>();  // ❌ throws unless registered
var dto = await handler.HandleAsync(new GetOrderByIdQuery(id), CancellationToken.None);
```

The fix is one line per handler in `AddXInfrastructure`:

```csharp
services.AddScoped<GetOrderByIdHandler>();
services.AddScoped<GetOrdersByBuyerHandler>();
```

`AddScoped<T>()` (single-type overload) registers the concrete type as both service-key and implementation. Scoped lifetime matches `DbContext`, which keeps the change tracker shared correctly. No interface is needed — there's nothing to substitute.

**How to spot whether you need this:** if you wrote a test calling `GetRequiredService<*Handler>()` for a handler that wasn't there before, also add the `AddScoped<*Handler>()` in the same diff. Reference: [CLAUDE.md "Communication Patterns → Wolverine handler discovery is NOT DI registration"](../CLAUDE.md). The failure mode that surfaced this rule was `OrderReadProjectionTests` breaking in CI after the repository-wrapper drop: pre-refactor the tests resolved `IOrderRepository` (which *was* registered), and the conversion to handler-resolved tests missed the equivalent registration. CI's `No service for type 'OrderService.Features.GetOrderByIdHandler' has been registered` was the first signal.

---

## 5. A Complete Request: Placing an Order

This section traces an order placement from HTTP request to event publication.

### Step 1 — HTTP Endpoint

```
POST /api/v1/orders
Authorization: Bearer <jwt>
{
  "buyerId": "...",
  "currency": "USD",
  "lines": [{ "productId": "...", "quantity": 2 }]
}
```

The endpoint in [OrderService/Endpoints/OrderEndpoints.cs](../OrderService/Endpoints/OrderEndpoints.cs) receives the request and dispatches the command through Wolverine. Routes are registered under a versioned route group (`/api/v1/...`) via the shared `MapV1ApiGroup` helper from `NextAurora.ServiceDefaults`:

```csharp
var orders = app.MapV1ApiGroup("Orders", "orders").RequireAuthorization();

orders.MapPost("/", async (PlaceOrderCommand command, IMessageBus bus, CancellationToken ct) =>
{
    var id = await bus.InvokeAsync<Guid>(command, ct);
    return Results.Created($"/api/v1/orders/{id}", new { id });
});
```

### Step 2 — Validation (FluentValidation policy)

Wolverine's FluentValidation policy (wired via `opts.UseFluentValidation()`) runs `PlaceOrderCommandValidator` before the handler. If `BuyerId` is empty or `Lines` is empty, the bus throws `ValidationException`, `GlobalExceptionHandler` maps it to an RFC 7807 400 response, and `PlaceOrderHandler.HandleAsync` is never invoked.

### Step 3 — Handler Validates Products via gRPC

`PlaceOrderHandler` calls `CatalogService` synchronously to validate each product:

```csharp
foreach (var lineItem in request.Lines)
{
    var product = await catalogClient.GetProductAsync(lineItem.ProductId, cancellationToken);

    if (product is null)       throw new InvalidOperationException("Product not found.");
    if (!product.IsAvailable)  throw new InvalidOperationException("Product not available.");
    if (product.StockQuantity < lineItem.Quantity)
                               throw new InvalidOperationException("Insufficient stock.");

    // Atomically deduct stock (prevents race conditions)
    var reserved = await catalogClient.ReserveStockAsync(lineItem.ProductId, lineItem.Quantity, cancellationToken);
    if (!reserved) throw new InvalidOperationException("Failed to reserve stock.");

    // Use server-side price — never trust client-submitted prices
    lines.Add(OrderLine.Create(product.Id, product.Name, lineItem.Quantity, product.Price));
}
```

`ICatalogClient` is an application-layer interface. The concrete implementation (`GrpcCatalogClient` in the Api layer) makes gRPC calls to `CatalogService`. This keeps the handler independent of the transport.

### Step 4 — Create and Persist the Order

```csharp
var order = Order.Create(request.BuyerId, request.Currency, lines);
await orderRepository.AddAsync(order, cancellationToken);
```

`Order.Create()` enforces domain invariants (non-empty buyer, at least one line). `IOrderRepository` is an interface in the Domain layer; the EF Core implementation is in Infrastructure.

### Step 5 — Stage the Event into the Outbox

```csharp
// Inside PlaceOrderHandler.HandleAsync
await orderRepository.AddAsync(order, ct);
OrdersPlaced.Add(1); // OpenTelemetry metric

// Cascading return: Wolverine sees the event and stages it into the outbox
// in the same transaction as the order insert. No bus.PublishAsync call needed.
return new HandlerResult<Guid>(order.Id, new OrderPlacedEvent
{
    OrderId = order.Id,
    BuyerId = order.BuyerId,
    /* ... */
});
```

**Why no `bus.PublishAsync(...)` call** — `opts.Policies.AutoApplyTransactions()` wraps the handler chain in an EF transaction, and `opts.Policies.UseDurableOutboxOnAllSendingEndpoints()` makes Wolverine stage outgoing messages to the `wolverine.outgoing_envelopes` table. The entity write and the outbox row commit *together*. A background dispatcher then forwards the staged messages to Azure Service Bus with retry. This eliminates the dual-write problem (entity saved but event publish crashed, or vice versa). Full rationale: [docs/performance-and-data-correctness.md "Resolved: transactional outbox via Wolverine"](performance-and-data-correctness.md#resolved-transactional-outbox-via-wolverine).

### Step 6 — HTTP Response

The endpoint returns `201 Created` with the new order ID and a versioned `Location: /api/v1/orders/{id}` header. The handler logs `Handled PlaceOrderCommand in <ms>ms` via Wolverine's `LogMessageStarting` policy, with correlation/user/session IDs in the logger scope from `ContextPropagationMiddleware`.

---

## 6. Service-to-Service Communication

The system uses two different communication patterns depending on whether the caller needs an immediate response.

### Synchronous: gRPC (OrderService → CatalogService)

Used when `PlaceOrderHandler` needs to validate products and reserve stock in real time.

```
OrderService  →  GrpcCatalogClient  →  (gRPC over HTTP/2)  →  CatalogService.Api  →  CatalogGrpcService
```

**CatalogService** defines the contract in a `.proto` file and implements the gRPC server:

```protobuf
// CatalogService.Api/Protos/catalog.proto
service CatalogGrpc {
  rpc GetProduct (GetProductRequest) returns (ProductResponse);
  rpc ReserveStock (ReserveStockRequest) returns (ReserveStockResponse);
}
```

`CatalogGrpcService` delegates to the same Wolverine handlers used by the REST API (via `bus.InvokeAsync<T>(...)`), so product retrieval and stock reservation logic is not duplicated.

**OrderService** registers the generated gRPC client and wraps it in `GrpcCatalogClient`:

```csharp
// OrderService/Program.cs
builder.Services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
{
    o.Address = new Uri("https+http://catalog-service"); // Aspire service discovery
});
builder.Services.AddScoped<ICatalogClient, GrpcCatalogClient>();
```

Aspire resolves `catalog-service` to the running instance automatically — no hardcoded URLs.

### Asynchronous: Azure Service Bus via Wolverine (all workflow events)

Used for the order fulfillment pipeline where immediate response isn't required. **Wolverine handles everything** — there is no hand-rolled `ServiceBusMessage` construction, no `ProcessMessageAsync` event handler, no manual `CompleteMessage` / `AbandonMessage` ack logic. Every concern below is configured once in `Program.cs` and the handler code is just a class with `HandleAsync`.

**Publishing.** A handler returns the event (cascading message) or calls `bus.PublishAsync(@event)`. The outbox-aware sending endpoint stages it into `wolverine.outgoing_envelopes` in the same DB transaction as the entity write; a background dispatcher forwards it to Azure Service Bus with retry. Headers (`X-Correlation-Id`, `X-User-Id`, `X-Session-Id`) are stamped onto outgoing envelopes by `OutgoingContextMiddleware` reading from `Activity` baggage — handler code stays clean.

**Consuming.** Wolverine subscribes to topics declared in `Program.cs`:

```csharp
opts.ListenToAzureServiceBusSubscription("order-events/payment-orders-sub")
    .FromTopic("order-events");
```

Wolverine then discovers handler classes for the message types and dispatches each incoming envelope to the right one. The pipeline around each consumer is the same as the HTTP-side one: FluentValidation (rare for events) → `ContextPropagationMiddleware` (restores the correlation/user/session scope from envelope headers) → `AutoApplyTransactions` → handler. Idempotency guards inside handlers (status checks, "already processed" lookups) handle Service Bus's at-least-once delivery.

**Retries and DLQ.** `opts.AddConcurrencyRetry()` retries `DbUpdateConcurrencyException` 3 times with 50/100/250ms cooldowns; transient transport failures use Wolverine's defaults. After retries are exhausted, the message goes to the Service Bus dead-letter queue and surfaces as the `messages.abandoned` metric.

---

## 7. Event-Driven Workflow

The full order lifecycle is driven by a choreography-based saga — no central orchestrator. Each service reacts to events independently.

```
1. Customer → POST /api/v1/orders
               ↓
         OrderService creates Order (status: Placed)
               ↓
         publishes OrderPlacedEvent → "order-events" topic
               ↓
   ┌───────────┴──────────┐
   ↓                      ↓
PaymentService         NotificationService
processes payment      sends "Order Received" email
   ↓
publishes PaymentCompletedEvent → "payment-events" topic
   ↓
   ┌───────────┴──────────┐
   ↓                      ↓
OrderService           ShippingService
marks Order as Paid    creates Shipment, assigns carrier + tracking
                           ↓
                       publishes ShipmentDispatchedEvent → "shipping-events" topic
                           ↓
               ┌───────────┴──────────┐
               ↓                      ↓
         OrderService           NotificationService
         marks Order as Shipped  sends "Order Shipped" email with tracking
```

### Event Contracts (NextAurora.Contracts)

All events are simple record classes in the shared `NextAurora.Contracts` project:

```csharp
public class OrderPlacedEvent
{
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public List<OrderLineContract> Lines { get; set; } = [];
}
```

Using a shared contracts project ensures all services agree on the same message shape.

### Idempotent Event Handlers

Because Service Bus delivers messages at-least-once, handlers guard against processing the same event twice:

```csharp
// PaymentCompletedHandler — idempotency guard
if (order.Status != OrderStatus.Placed) return;  // already processed, skip
order.MarkAsPaid();

// ProcessPaymentHandler — idempotency guard
var existing = await repository.GetByOrderIdAsync(request.OrderId, cancellationToken);
if (existing is not null) return existing.Id;     // already processed, return existing ID
```

### Two correctness guarantees that aren't visible in the code

1. **Transactional outbox.** Each event-publishing service (Order, Payment, Shipping) persists outgoing messages to a `wolverine` schema in its own DB. The entity write and the outbox-row write commit in the same EF transaction. Either both happen or neither does — no more lost events on bus failure or process crash. Wired in `Program.cs` via `PersistMessagesWith{SqlServer,Postgresql}` + `AutoApplyTransactions` + `UseDurableOutboxOnAllSendingEndpoints`.

2. **Optimistic concurrency tokens.** Every updatable aggregate carries a concurrency token: Postgres `xmin` shadow property (Catalog Product/Category, Shipping Shipment) and SQL Server `RowVersion` shadow property (Order, Payment, Refund). Two concurrent handlers attempting to mutate the same aggregate produce `DbUpdateConcurrencyException` on the second `SaveChanges`. For HTTP commands, `GlobalExceptionHandler` maps it to 409 Conflict. For event handlers, `opts.AddConcurrencyRetry()` retries 3 times with backoff before DLQing. This is the difference between "we coordinate" and "the last write silently wins." Full rationale: [docs/performance-and-data-correctness.md](performance-and-data-correctness.md).

---

## 8. Cross-Cutting Concerns

These concerns are handled consistently across all services.

### Input Validation

Three layers of validation catch invalid data at different points:

| Layer | Mechanism | When it runs |
|-------|-----------|--------------|
| **HTTP / messaging** | FluentValidation via Wolverine's `UseFluentValidation()` policy | Before any handler executes (HTTP-dispatched and async-dispatched alike) |
| **Domain** | `ArgumentException` / `ArgumentOutOfRangeException` in `Create()` / mutation methods | When domain objects are constructed or modified |
| **Business rules** | `InvalidOperationException` in domain methods | When state transitions are attempted |

### Error Handling

`GlobalExceptionHandler` (in `NextAurora.ServiceDefaults`) converts all unhandled exceptions to RFC 7807 `ProblemDetails` responses. Internal details (product IDs, stack traces) are logged server-side and never sent to the client:

| Exception type | HTTP status | Client message |
|---------------|-------------|----------------|
| `ValidationException` | 400 | Grouped field errors |
| `ArgumentException` | 400 | "One or more request parameters are invalid." |
| `InvalidOperationException` | 409 | "The requested operation is not valid for the current state." |
| Anything else | 500 | "Please contact support with the trace ID." |

Every error response includes a `traceId` that links to the full server-side log.

### Correlation, User, and Session ID Propagation

Three context identifiers flow through every request — HTTP and async — automatically. There are no per-handler reads or writes; the middleware does it all.

| Concept | Source | HTTP / Service Bus header | Logger scope key |
|---------|--------|---------------------------|------------------|
| Correlation | `X-Correlation-Id` header, or generated from trace ID | `X-Correlation-Id` | `CorrelationId` |
| User | JWT `sub` claim (`ClaimTypes.NameIdentifier`) | `X-User-Id` | `UserId` |
| Session | `X-Session-Id` request header (client-supplied) | `X-Session-Id` | `SessionId` |

Three pieces of middleware do the work:

- **`CorrelationIdMiddleware`** — HTTP entry point. Sets all three IDs into `Activity` baggage and opens a `logger.BeginScope`. Echoes the correlation ID in the response header.
- **`ContextPropagationMiddleware`** — Wolverine incoming-message middleware (async entry point). Reads the same headers from `Envelope.Headers`, restores them into `Activity` baggage, and opens a logger scope around the handler.
- **`OutgoingContextMiddleware`** — Wolverine outgoing middleware. Reads `Activity` baggage and stamps the same headers onto outgoing envelopes, so the next consumer sees the same IDs.

All three are wired in each service's `Program.cs` via the `opts.AddNextAuroraContextPropagation()` extension. Detail: [docs/context-propagation.md](context-propagation.md) and CLAUDE.md "Observability & Context Propagation."

### Structured Logging and Tracing

Wolverine's `opts.Policies.LogMessageStarting(LogLevel.Information)` logs handler start + elapsed time around every dispatched message. Because `ContextPropagationMiddleware` opens the logger scope first, every log line emitted *anywhere inside the handler* (repository calls, gateway calls, custom logger calls) carries `CorrelationId`, `UserId`, and `SessionId` automatically. Combined with OpenTelemetry distributed tracing through OTLP into the Aspire dashboard (dev) or Application Insights (production), the full span tree + structured fields are queryable end-to-end.

---

## 9. Infrastructure and Local Development (Aspire)

[NextAurora.AppHost/AppHost.cs](../NextAurora.AppHost/AppHost.cs) is the single entry point for local development. Running it starts the entire distributed system:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Databases
var catalogDb  = builder.AddPostgres("catalog-pg").AddDatabase("catalog-db");
var ordersDb   = builder.AddSqlServer("orders-sql").AddDatabase("orders-db");
var paymentsDb = builder.AddSqlServer("payments-sql").AddDatabase("payments-db");
var shippingDb = builder.AddPostgres("shipping-pg").AddDatabase("shipping-db");

// L2 cache + messaging — Aspire 13+ requires explicit local-dev fallbacks
var redis      = builder.AddRedis("cache");
var serviceBus = builder.AddAzureServiceBus("messaging").RunAsEmulator();   // mandatory in Aspire 13+

// App Insights only when publishing — no local emulator exists
IResourceBuilder<AzureApplicationInsightsResource>? insights = null;
if (builder.ExecutionContext.IsPublishMode)
{
    insights = builder.AddAzureApplicationInsights("insights");
}

// Service Bus topology — subscription names are GLOBALLY UNIQUE in the namespace
var orderTopic = serviceBus.AddServiceBusTopic("order-events");
orderTopic.AddServiceBusSubscription("payment-orders-sub");
orderTopic.AddServiceBusSubscription("notify-orders-sub");
// ... payment-events and shipping-events topics, each with consumer-prefixed subs

// Services with their dependencies — every WithReference gets a matching WaitFor
// because Aspire 13's WithReference no longer waits for healthy
builder.AddProject<Projects.OrderService_Api>("order-service")
    .WithReference(ordersDb).WaitFor(ordersDb)
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithReference(catalogService).WaitFor(catalogService);  // gRPC service discovery
```

Aspire handles:
- Spinning up Docker containers for each database, Redis, Keycloak, and the Service Bus emulator
- Injecting connection strings into each service automatically
- Resolving service names (`catalog-service`) to the correct URL
- Health-check aggregation in the dashboard

Every service calls `builder.AddServiceDefaults()` in `Program.cs` to register shared telemetry (OpenTelemetry → OTLP), health checks, the resilience handler (`Microsoft.Extensions.Http.Resilience`), JWT bearer auth, the global exception handler, API versioning, and the correlation/user/session middleware automatically.

**Aspire 13 gotchas the AppHost has to handle** (each captured in CLAUDE.md after surfacing):
- Aspire SDK and runtime package versions must match exactly (or SDK ≥ packages).
- Service Bus subscription names are globally unique in the namespace — convention is `{consumer}-{source}-sub` (e.g., `payment-orders-sub`).
- `AddAzureServiceBus(...)` requires a chained `.RunAsEmulator()` for local runs.
- `AddAzureApplicationInsights(...)` has no local emulator — gate it on `IsPublishMode`.
- Every `.WithReference(x)` on a non-trivial dependency needs a matching `.WaitFor(x)` since `WithReference` no longer waits for healthy in Aspire 13.

### Dev-time API exploration

Each service emits its OpenAPI document and ships an interactive UI:

| Endpoint | Purpose |
|---|---|
| `GET /openapi/v1.json` | Machine-readable spec (used by Scalar, gateways, codegen) |
| `GET /openapi/v1.yaml` | Same spec, YAML form (Spectral/CI, embedding in markdown) |
| `GET /scalar/v1` | **Scalar** interactive API reference UI (try-it-out, search) |

All three are gated behind `app.Environment.IsDevelopment()` — production exposes nothing. Scalar reads the OpenAPI doc, so it picks up versioning, auth requirements, and rate-limit annotations automatically.

### Health Checks

Every service exposes two endpoints:

- `GET /health` — readiness probe; all registered checks must pass (DB connectivity, etc.)
- `GET /alive` — liveness probe; self-check only

Database health checks are registered in each service's Infrastructure `DependencyInjection.cs`:

```csharp
services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>();
```

---

## 10. Testing Strategy

All tests are unit tests organized per service under `tests/`. Each test project mirrors the service's Application and Domain layers.

### Naming Convention

```
MethodName_Condition_ExpectedResult
```

Examples:
- `Handle_WhenProductNotFound_ThrowsInvalidOperationException`
- `Create_WhenBuyerIdIsEmpty_ThrowsArgumentException`
- `Handle_WhenPaymentExistsForOrder_ReturnsExistingPaymentId`

### Test Builders

Each test project includes builder classes to reduce boilerplate. For example, `OrderBuilder` creates a valid `Order` in one line, with optional overrides for specific scenarios:

```csharp
var order = new OrderBuilder().WithStatus(OrderStatus.Paid).Build();
```

### What Is Tested

| Category | Coverage |
|----------|---------|
| Domain entities | Factory method validation, state transition guards |
| Application handlers | Happy path, error paths, idempotency guards |
| Validators | Required fields, value ranges, format checks |

Integration tests (Testcontainers-based) are listed as a future item in the BRD.

### Running Tests

```bash
dotnet test
```

All tests in the solution run. Each test project targets the unit tests for one service.

---

## 11. Where to Look for What

| I want to... | Look here |
|--------------|-----------|
| Add a new API endpoint | `{Service}.Api/Endpoints/` (use `MapV1ApiGroup(...)` so the route lives under `/api/v1/...`) |
| Add a new command or query | `{Service}.Application/Commands/` or `Queries/` |
| Add a handler for a command/event | `{Service}.Application/Handlers/` (Wolverine discovers by convention — no interface to implement) |
| Add validation for a command | `{Service}.Application/Validators/` |
| Change a domain business rule | `{Service}.Domain/Entities/` |
| Add a new event type | `NextAurora.Contracts/Events/` |
| Change which events a service publishes | Return them as cascading messages from the handler, or `bus.PublishAsync` |
| Change which events a service consumes | Add a handler class for the event in `{Service}.Application/Handlers/`, plus an `opts.ListenToAzureServiceBusSubscription(...)` line in `{Service}.Api/Program.cs` |
| Inspect outgoing events / outbox state | Each event-publishing service's DB has a `wolverine` schema; `outgoing_envelopes` is the staged-but-not-yet-flushed queue, `dead_letters` the DLQ. See [event-replay.md](./event-replay.md) |
| Add a new gRPC method to CatalogService | `CatalogService.Api/Protos/catalog.proto` + `CatalogService.Api/Services/CatalogGrpcService.cs` (regenerate clients in OrderService) |
| Add a cached read query in Catalog | `IProductCache.GetOrLoadAsync(id, factory)` — see [HybridProductCache.cs](../CatalogService/CatalogService.Infrastructure/Caching/HybridProductCache.cs) |
| Reach for raw SQL via Dapper | `ctx.Database.GetDbConnection()` so it shares the EF transaction — see [Dapper escape hatch](performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch) |
| Understand the full order lifecycle | This guide, [architecture.md](./architecture.md), the [architecture diagram](./nextaurora-architecture.svg) ([source](./nextaurora-architecture.excalidraw)), and the event flow diagram in [README.md](../README.md) |
| Understand performance + correctness rules (outbox, concurrency tokens, caching) | [performance-and-data-correctness.md](./performance-and-data-correctness.md) |
| Understand observability/logging/tracing | [observability.md](./observability.md), [context-propagation.md](./context-propagation.md) |
| Understand event-replay / outbox tooling | [event-replay.md](./event-replay.md) |
| Understand what is and isn't implemented | [BRD.md](./BRD.md) (requirement status table), [STATUS.md](./STATUS.md) (cross-session entry point) |

---

*For the architectural diagrams and communication matrix, see [architecture.md](./architecture.md). For business requirements and implementation status, see [BRD.md](./BRD.md).*
