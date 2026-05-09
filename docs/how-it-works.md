# How NextAurora Works — Developer Guide

This guide explains how the code is organized, how requests flow through the system, and how the services communicate. It is intended for developers who want to understand the codebase quickly.

---

## Table of Contents

1. [Project Layout](#1-project-layout)
2. [Clean Architecture in Every Service](#2-clean-architecture-in-every-service)
3. [Domain Model — Rich Entities with Guard Clauses](#3-domain-model--rich-entities-with-guard-clauses)
4. [CQRS + MediatR — The Request Pipeline](#4-cqrs--mediatr--the-request-pipeline)
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

  CatalogService/
    CatalogService.Domain/         # Product, Category entities; repository interfaces
    CatalogService.Application/    # Commands, queries, MediatR handlers, validators
    CatalogService.Infrastructure/ # EF Core (PostgreSQL), repositories, gRPC server
    CatalogService.Api/            # ASP.NET Core host, REST endpoints, gRPC server

  OrderService/       (same 4-layer structure)
  PaymentService/     (same 4-layer structure)
  ShippingService/    (same 4-layer structure)
  NotificationService/(same 4-layer structure, no database)

  Storefront/        # Blazor WASM — customer-facing SPA (scaffold only)
  SellerPortal/      # Blazor Server — merchant dashboard (scaffold only)

  tests/
    OrderService.Tests.Unit
    CatalogService.Tests.Unit
    PaymentService.Tests.Unit
    ShippingService.Tests.Unit
    NotificationService.Tests.Unit
```

---

## 2. Clean Architecture in Every Service

Every backend service uses the same four-layer structure. The dependency rule is strict and enforced by project references:

```
Domain          →  no dependencies
Application     →  Domain only
Infrastructure  →  Domain + Application
Api             →  all layers (DI composition root)
```

| Layer | What lives here |
|-------|----------------|
| **Domain** | Entities, value objects, enums, repository and publisher interfaces |
| **Application** | Commands, queries, MediatR handlers, FluentValidation validators, MediatR pipeline behaviors |
| **Infrastructure** | EF Core `DbContext`, repositories, Service Bus publisher/processor, external gateways (Stripe, etc.) |
| **Api** | ASP.NET Core `Program.cs`, endpoint mapping, gRPC services/clients, DI wiring |

The **Domain** layer has zero dependencies on any framework. The **Application** layer only knows about domain types and its own command/query objects. Infrastructure concerns (databases, message brokers) are injected through interfaces defined in the Domain or Application layer.

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

## 4. CQRS + MediatR — The Request Pipeline

All business operations are expressed as either a **Command** (changes state, returns an ID or nothing) or a **Query** (reads data, returns a DTO). MediatR dispatches them to the correct handler.

### Command Example

```csharp
// Application/Commands/PlaceOrderCommand.cs
public record PlaceOrderCommand(Guid BuyerId, string Currency, List<OrderLineItem> Lines)
    : IRequest<Guid>;

// Application/Validators/PlaceOrderCommandValidator.cs
public class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.BuyerId).NotEmpty();
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Lines).NotEmpty();
    }
}

// Application/Handlers/PlaceOrderHandler.cs
public class PlaceOrderHandler(...) : IRequestHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        // 1. Validate products via gRPC
        // 2. Reserve stock
        // 3. Create Order aggregate
        // 4. Persist to database
        // 5. Publish OrderPlacedEvent
        return order.Id;
    }
}
```

### Pipeline Behaviors (Run Before Every Handler)

Two pipeline behaviors run in order before any handler executes:

```
HTTP request
  → FluentValidation (ValidationBehavior) — rejects invalid input with 400
  → LoggingBehavior — logs handler name + correlation ID + elapsed time
  → Handler
```

`ValidationBehavior` runs validators from the Application layer. If validation fails, it throws `FluentValidation.ValidationException`, which is caught by `GlobalExceptionHandler` and returned as a structured 400 response — the handler is never reached.

`LoggingBehavior` uses a `try/finally` block so it always logs the outcome even if the handler throws:

```csharp
// OrderService.Application/Behaviors/LoggingBehavior.cs
var succeeded = false;
try
{
    var response = await next();
    succeeded = true;
    return response;
}
finally
{
    sw.Stop();
    if (succeeded)
        logger.LogInformation("Handled {RequestName} in {ElapsedMs}ms ...");
    else
        logger.LogWarning("Failed {RequestName} after {ElapsedMs}ms ...");
}
```

---

## 5. A Complete Request: Placing an Order

This section traces an order placement from HTTP request to event publication.

### Step 1 — HTTP Endpoint

```
POST /api/orders
{
  "buyerId": "...",
  "currency": "USD",
  "lines": [{ "productId": "...", "quantity": 2 }]
}
```

The endpoint in `OrderService.Api/Endpoints/OrderEndpoints.cs` receives the request and dispatches a MediatR command:

```csharp
app.MapPost("/api/orders", async (PlaceOrderCommand command, ISender sender) =>
{
    var id = await sender.Send(command);
    return Results.Created($"/api/orders/{id}", new { id });
});
```

### Step 2 — Validation (ValidationBehavior)

`PlaceOrderCommandValidator` runs automatically before the handler. If `BuyerId` is empty or `Lines` is empty, the request is rejected with HTTP 400 before any domain logic runs.

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

### Step 5 — Publish the Event

```csharp
var @event = new OrderPlacedEvent { OrderId = order.Id, ... };
await eventPublisher.PublishAsync(@event, "order-events", cancellationToken);
OrdersPlaced.Add(1); // OpenTelemetry metric
return order.Id;
```

`IEventPublisher` is a Domain interface. In Infrastructure, it is implemented by `LoggingEventPublisher` (which wraps `ServiceBusEventPublisher` using the Decorator pattern — see [Event Replay Guide](./event-replay.md)).

### Step 6 — HTTP Response

The endpoint returns `201 Created` with the new order ID. The handler never throws for the happy path, so `LoggingBehavior` logs success.

---

## 6. Service-to-Service Communication

The system uses two different communication patterns depending on whether the caller needs an immediate response.

### Synchronous: gRPC (OrderService → CatalogService)

Used when `PlaceOrderHandler` needs to validate products and reserve stock in real time.

```
OrderService.Api  →  GrpcCatalogClient  →  (gRPC over HTTP/2)  →  CatalogService.Api  →  CatalogGrpcService
```

**CatalogService** defines the contract in a `.proto` file and implements the gRPC server:

```protobuf
// CatalogService.Api/Protos/catalog.proto
service CatalogGrpc {
  rpc GetProduct (GetProductRequest) returns (ProductResponse);
  rpc ReserveStock (ReserveStockRequest) returns (ReserveStockResponse);
}
```

`CatalogGrpcService` delegates to the same MediatR handlers used by the REST API, so product retrieval and stock reservation logic is not duplicated.

**OrderService** registers the generated gRPC client and wraps it in `GrpcCatalogClient`:

```csharp
// OrderService.Api/Program.cs
builder.Services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
{
    o.Address = new Uri("https+http://catalog-service"); // Aspire service discovery
});
builder.Services.AddScoped<ICatalogClient, GrpcCatalogClient>();
```

Aspire resolves `catalog-service` to the running instance automatically — no hardcoded URLs.

### Asynchronous: Azure Service Bus (all workflow events)

Used for the order fulfillment pipeline where immediate response is not required.

**Publishing** (e.g., in `ServiceBusEventPublisher`):

```csharp
var message = new ServiceBusMessage(JsonSerializer.Serialize(@event))
{
    Subject = typeof(T).Name,               // e.g., "OrderPlacedEvent"
    CorrelationId = correlationId           // for tracing
};
message.ApplicationProperties["X-Correlation-Id"] = correlationId;
await sender.SendMessageAsync(message, ct);
```

**Consuming** (e.g., in `ServiceBusEventProcessor`):

```csharp
processor.ProcessMessageAsync += async args =>
{
    var correlationId = args.Message.ApplicationProperties
        .TryGetValue("X-Correlation-Id", out var cid) ? cid?.ToString() : null;

    using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["CorrelationId"] = correlationId,
        ["MessageId"]     = args.Message.MessageId,
        ["DeliveryCount"] = args.Message.DeliveryCount
    });

    try
    {
        // Deserialize → dispatch via MediatR → CompleteMessage
        await args.CompleteMessageAsync(args.Message, stoppingToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to process {Subject}. Abandoning for retry/DLQ", args.Message.Subject);
        await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
    }
};
```

Abandoning a message increments its `DeliveryCount`. When `DeliveryCount` reaches the subscription's `MaxDeliveryCount`, Azure Service Bus automatically moves it to the Dead Letter Queue (DLQ).

---

## 7. Event-Driven Workflow

The full order lifecycle is driven by a choreography-based saga — no central orchestrator. Each service reacts to events independently.

```
1. Customer → POST /api/orders
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

---

## 8. Cross-Cutting Concerns

These concerns are handled consistently across all services.

### Input Validation

Three layers of validation catch invalid data at different points:

| Layer | Mechanism | When it runs |
|-------|-----------|--------------|
| **HTTP** | FluentValidation + `ValidationBehavior` | Before any handler executes |
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

### Correlation ID Propagation

`CorrelationIdMiddleware` (registered in `ServiceDefaults`) runs on every HTTP request:

1. Reads `X-Correlation-Id` from the request header (generates one if absent)
2. Stores it in `Activity` baggage so it propagates through gRPC and HTTP client calls automatically
3. Opens a logger scope so every log line in that request includes the correlation ID
4. Echoes it in the response `X-Correlation-Id` header

Service Bus publishers inject the correlation ID into each message. Processors extract it and open a logging scope, so the same ID appears in logs across all services for a single transaction.

### Structured Logging and Tracing

`LoggingBehavior` in each service's Application layer logs the start and end of every MediatR handler with the correlation ID and elapsed time. Combined with OpenTelemetry distributed tracing, you can see the complete span tree for any request in the Aspire dashboard during development.

---

## 9. Infrastructure and Local Development (Aspire)

`NextAurora.AppHost/AppHost.cs` is the single entry point for local development. Running it starts the entire distributed system:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Databases
var catalogDb  = builder.AddPostgres("catalog-pg").AddDatabase("catalog-db");
var ordersDb   = builder.AddSqlServer("orders-sql").AddDatabase("orders-db");
var paymentsDb = builder.AddSqlServer("payments-sql").AddDatabase("payments-db");
var shippingDb = builder.AddPostgres("shipping-pg").AddDatabase("shipping-db");

// Cache and messaging
var redis      = builder.AddRedis("cache");
var serviceBus = builder.AddAzureServiceBus("messaging");

// Service Bus topology
var orderTopic   = serviceBus.AddServiceBusTopic("order-events");
orderTopic.AddServiceBusSubscription("payment-sub");   // PaymentService reads this
orderTopic.AddServiceBusSubscription("notify-sub");    // NotificationService reads this
// ... payment-events and shipping-events topics

// Services with their dependencies injected
builder.AddProject<Projects.OrderService_Api>("order-service")
    .WithReference(ordersDb)
    .WithReference(serviceBus)
    .WithReference(catalogService);  // enables gRPC service discovery
```

Aspire handles:
- Spinning up Docker containers for each database
- Running the Azure Service Bus emulator
- Injecting connection strings into each service automatically
- Resolving service names (`catalog-service`) to the correct URL

Every service calls `builder.AddServiceDefaults()` in `Program.cs` to register shared telemetry, health checks, resilience handlers, and middleware automatically.

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
| Add a new API endpoint | `{Service}.Api/Endpoints/` |
| Add a new command or query | `{Service}.Application/Commands/` or `Queries/` |
| Add a handler for a command | `{Service}.Application/Handlers/` |
| Add validation for a command | `{Service}.Application/Validators/` |
| Change a domain business rule | `{Service}.Domain/Entities/` |
| Add a new event type | `NextAurora.Contracts/Events/` |
| Change how events are published | `{Service}.Infrastructure/Messaging/` |
| Change how events are consumed | `{Service}.Infrastructure/Messaging/ServiceBusEventProcessor.cs` |
| Add a new gRPC method to CatalogService | `CatalogService.Api/Protos/catalog.proto` + `CatalogService.Api/Services/CatalogGrpcService.cs` |
| Understand the full order lifecycle | This guide, [architecture.md](./architecture.md), and the event flow diagram in [README.md](../README.md) |
| Understand observability/logging | [observability.md](./observability.md) |
| Understand event replay and the admin API | [event-replay.md](./event-replay.md) |
| Understand what is and isn't implemented | [BRD.md](./BRD.md) (requirement status table) |

---

*For the architectural diagrams and communication matrix, see [architecture.md](./architecture.md). For business requirements and implementation status, see [BRD.md](./BRD.md).*
