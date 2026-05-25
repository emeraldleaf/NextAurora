# OrderService — code flow walkthrough

> **What this is.** A walk through the code paths a new contributor will hit first in [OrderService](../../OrderService/). OrderService is the **saga orchestrator** — every order placed here triggers a multi-step workflow that fans out to PaymentService, ShippingService, and NotificationService over Azure Service Bus, then comes back through three event handlers that mutate the Order aggregate. The diagrams below show *which files, classes, and interfaces* get touched at each step, in the order they actually execute.
>
> **Architecture style:** Vertical Slice Architecture (single csproj). Folders: [`Endpoints/`](../../OrderService/Endpoints), [`Features/`](../../OrderService/Features), [`Domain/`](../../OrderService/Domain), [`Infrastructure/`](../../OrderService/Infrastructure). Composition root: [`Program.cs`](../../OrderService/Program.cs).
>
> **Two flows to understand:**
> 1. **Phase 1 — Request-driven (PlaceOrder):** buyer POSTs an order, OrderService validates against Catalog over gRPC, persists, and publishes `OrderPlacedEvent` via the transactional outbox.
> 2. **Phase 2 — Event-driven (saga consume):** three events come back over Service Bus — `PaymentCompletedEvent`, `PaymentFailedEvent`, `ShipmentDispatchedEvent` — each one transitions the Order through its state machine.

---

## Phase 1 — Place order (request-driven)

```mermaid
sequenceDiagram
    autonumber
    actor Buyer
    participant EP as OrderEndpoints<br/>Endpoints/OrderEndpoints.cs
    participant Bus as IMessageBus<br/>(Wolverine)
    participant MW as ContextPropagation +<br/>FluentValidation middleware
    participant H as PlaceOrderHandler<br/>Features/PlaceOrder.cs
    participant gRPC as ICatalogClient<br/>GrpcCatalogClient.cs
    participant Cat as CatalogService<br/>(separate service)
    participant Agg as Order aggregate<br/>Domain/Order.cs
    participant Repo as IOrderRepository<br/>OrderRepository.cs
    participant Pub as IEventPublisher<br/>WolverineEventPublisher.cs
    participant DB as SQL Server +<br/>wolverine.outgoing_envelopes
    participant ASB as Azure Service Bus<br/>(orders topic)

    Buyer->>EP: POST /api/v1/orders<br/>{ BuyerId, Currency, Lines[] }
    Note over EP: JWT sub == command.BuyerId?<br/>else 403 Forbid
    EP->>Bus: bus.InvokeAsync<Guid>(command, ct)
    Bus->>MW: opens logger scope<br/>(CorrelationId, UserId, SessionId)<br/>FluentValidation runs
    MW->>H: HandleAsync(command, ct)<br/>(wrapped by AutoApplyTransactions)

    par for each line — validate
        H->>gRPC: GetProductAsync(productId)
        gRPC->>Cat: gRPC call
        Cat-->>gRPC: ProductDto
        gRPC-->>H: ProductDto
    end
    Note over H: throw InvalidOperationException<br/>if missing / unavailable /<br/>insufficient stock

    par for each line — reserve
        H->>gRPC: ReserveStockAsync(productId, qty)
        gRPC->>Cat: gRPC call (writes Catalog DB)
        Cat-->>gRPC: bool
        gRPC-->>H: bool
    end

    H->>Agg: Order.Create(buyerId, currency, lines)
    Note over Agg: factory validates invariants —<br/>uses CatalogService prices,<br/>NEVER client-submitted prices
    H->>Repo: AddAsync(order, ct)
    Repo->>DB: INSERT Orders + OrderLines
    H->>Pub: PublishAsync(OrderPlacedEvent)
    Note over Pub,DB: Wolverine stages envelope into<br/>wolverine.outgoing_envelopes<br/>(same DB transaction as entity write —<br/>UseDurableOutboxOnAllSendingEndpoints)
    DB-->>H: tx commit
    H-->>Bus: order.Id (Guid)
    Bus-->>EP: order.Id
    EP-->>Buyer: 202 Accepted<br/>Location: /api/v1/orders/{id}

    Note over DB,ASB: Wolverine background flush<br/>dispatches envelope to ASB
    DB->>ASB: OrderPlacedEvent
```

**Key wiring (in [`Program.cs`](../../OrderService/Program.cs)):**

```csharp
opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
opts.UseEntityFrameworkCoreTransactions();
opts.Policies.AutoApplyTransactions();
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
opts.AddNextAuroraContextPropagation();   // logger scope from ServiceDefaults
opts.UseFluentValidation();
opts.AddConcurrencyRetry();               // OnException<DbUpdateConcurrencyException>
```

**Why two parallel `Task.WhenAll` blocks** (validate, then reserve) instead of one combined pass: a validation failure on line 3 must NOT leave reservations on lines 1 and 2. Splitting the phases makes partial-commit impossible at the validation layer. See [PlaceOrder.cs:73-79](../../OrderService/Features/PlaceOrder.cs#L73) for the rationale in code.

**`DbContext` thread-safety**: the `Task.WhenAll` parallelism is over **gRPC client calls only** — no OrderService `DbContext` is touched in that block. The CLAUDE.md "DbContext is not thread-safe" rule is satisfied. Each gRPC call hits CatalogService where it gets its own per-request DbContext scope.

---

## Phase 2 — Saga consume (event-driven)

Three events come back over Service Bus. They all follow the same shape: ASB → Wolverine consumer → middleware restores logger scope from envelope headers → handler loads the tracked `Order` aggregate → calls a named state-transition method → `SaveChanges`. The state-transition method enforces idempotency via a status guard (a duplicate event no-ops instead of throwing).

```mermaid
sequenceDiagram
    autonumber
    participant ASB as Azure Service Bus<br/>(payments topic, shipping topic)
    participant W as Wolverine consumer +<br/>ContextPropagation middleware
    participant H as Saga handler<br/>(one of 3 below)
    participant Repo as IOrderRepository
    participant Agg as Order aggregate<br/>Domain/Order.cs
    participant DB as SQL Server<br/>(orders + wolverine schema)

    ASB->>W: PaymentCompletedEvent<br/>(or PaymentFailedEvent /<br/> ShipmentDispatchedEvent)
    Note over W: reads X-Correlation-Id,<br/>X-User-Id, X-Session-Id<br/>from envelope headers,<br/>opens logger scope
    W->>H: HandleAsync(@event, ct)<br/>(AutoApplyTransactions wraps)

    H->>Repo: GetByIdAsync(@event.OrderId, ct)
    Repo->>DB: SELECT * FROM Orders<br/>WHERE Id = @id (tracked)
    DB-->>Repo: Order entity (tracked) +<br/>RowVersion snapshot
    Repo-->>H: Order

    alt order missing (at-least-once delivery edge)
        H-->>W: return (no-op)
        Note over H: late-arriving event<br/>against deleted order
    else order found
        H->>Agg: MarkAsPaid() /<br/>MarkAsPaymentFailed() /<br/>MarkAsShipped()
        Note over Agg: STATE GUARD inside —<br/>only transitions if current<br/>status matches expected<br/>(idempotency under<br/>at-least-once delivery)
        Agg-->>H: void / no-op on duplicate

        H->>Repo: UpdateAsync(order, ct)
        Repo->>DB: UPDATE Orders<br/>SET ..., RowVersion = NEW<br/>WHERE Id = @id AND RowVersion = @v
        alt RowVersion matches
            DB-->>H: 1 row affected (tx commit)
        else concurrency conflict
            DB-->>H: 0 rows → DbUpdateConcurrencyException
            Note over H: AddConcurrencyRetry policy —<br/>3× retry with backoff,<br/>then DLQ
        end
    end
```

**The three saga handlers all sit in [`Features/`](../../OrderService/Features):**

| Event consumed | Handler | State transition | Aggregate method |
|---|---|---|---|
| `PaymentCompletedEvent` | [PaymentCompletedHandler.cs](../../OrderService/Features/PaymentCompletedHandler.cs) | `Placed → Paid` | `Order.MarkAsPaid()` |
| `PaymentFailedEvent` | [PaymentFailedHandler.cs](../../OrderService/Features/PaymentFailedHandler.cs) | `Placed → PaymentFailed` | `Order.MarkAsPaymentFailed()` |
| `ShipmentDispatchedEvent` | [ShipmentDispatchedHandler.cs](../../OrderService/Features/ShipmentDispatchedHandler.cs) | `Paid → Shipped` | `Order.MarkAsShipped()` |

---

## Order aggregate — state machine

The state machine is enforced *inside* the aggregate methods, not by the handlers or the bus. A handler call to `MarkAsPaid()` on an already-`Paid` order is a no-op, not a throw. This is the **idempotency guard** that makes at-least-once delivery safe.

```mermaid
stateDiagram-v2
    [*] --> Placed: Order.Create()<br/>(PlaceOrderHandler)
    Placed --> Paid: MarkAsPaid()<br/>(PaymentCompletedHandler)
    Placed --> PaymentFailed: MarkAsPaymentFailed()<br/>(PaymentFailedHandler)
    Paid --> Shipped: MarkAsShipped()<br/>(ShipmentDispatchedHandler)
    Shipped --> [*]
    PaymentFailed --> [*]

    note right of Placed
        Initial state on
        successful PlaceOrder
    end note

    note right of Paid
        Awaiting shipment
        from ShippingService
    end note
```

---

## Read-path coexistence (CQRS data-access split)

The same [`IOrderRepository`](../../OrderService/Domain/IOrderRepository.cs) interface carries **both** write-loader methods (used by the sagas above) and DTO-returning read-projection methods (used by the GET endpoints). The split is part of the project's [CQRS data-access rule](../cqrs-data-access.md) — read paths project to DTOs inside the IQueryable to skip entity materialization and avoid parent-cartesian rows from collection includes.

```mermaid
graph LR
    subgraph Domain["Domain/IOrderRepository.cs"]
        I["interface IOrderRepository"]
    end

    subgraph Impl["Infrastructure/OrderRepository.cs"]
        WL1["GetByIdAsync → Order<br/>(tracked, Include Lines)"]
        WL2["AddAsync, UpdateAsync"]
        RP1["GetSummaryByIdAsync → OrderSummaryDto<br/>(AsNoTracking + projection)"]
        RP2["GetSummariesByBuyerIdAsync → IReadOnlyList&lt;OrderSummaryDto&gt;<br/>(AsNoTracking + projection)"]
    end

    subgraph Writers["Saga + command handlers"]
        SH["PaymentCompletedHandler<br/>PaymentFailedHandler<br/>ShipmentDispatchedHandler<br/>PlaceOrderHandler"]
    end

    subgraph Readers["Query handlers"]
        GH["GetOrderByIdHandler<br/>GetOrdersByBuyerHandler"]
    end

    I --> WL1
    I --> WL2
    I --> RP1
    I --> RP2

    SH -.->|tracked entity| WL1
    SH -.-> WL2
    GH -.->|DTO directly| RP1
    GH -.-> RP2

    style WL1 fill:#dbeafe,stroke:#1e3a5f
    style WL2 fill:#dbeafe,stroke:#1e3a5f
    style RP1 fill:#a7f3d0,stroke:#047857
    style RP2 fill:#a7f3d0,stroke:#047857
```

The method signature is the contract: anything returning a domain entity is a write loader; anything returning a DTO is a read projection. Mixing the two is the anti-pattern the rule exists to prevent.

---

## File inventory

| Path | Purpose |
|---|---|
| [Endpoints/OrderEndpoints.cs](../../OrderService/Endpoints/OrderEndpoints.cs) | HTTP surface: POST/GET buyer-scoped, defense-in-depth JWT check |
| [Features/PlaceOrder.cs](../../OrderService/Features/PlaceOrder.cs) | Command + validator + handler (the entry to the saga) |
| [Features/GetOrderById.cs](../../OrderService/Features/GetOrderById.cs) | Single-order read; delegates to read-projection method |
| [Features/GetOrdersByBuyer.cs](../../OrderService/Features/GetOrdersByBuyer.cs) | Paginated buyer history; delegates to read-projection method |
| [Features/PaymentCompletedHandler.cs](../../OrderService/Features/PaymentCompletedHandler.cs) | Saga step 2a: payment succeeded → mark paid |
| [Features/PaymentFailedHandler.cs](../../OrderService/Features/PaymentFailedHandler.cs) | Saga step 2b: payment failed → mark failed |
| [Features/ShipmentDispatchedHandler.cs](../../OrderService/Features/ShipmentDispatchedHandler.cs) | Saga step 3: shipment dispatched → mark shipped |
| [Domain/Order.cs](../../OrderService/Domain/Order.cs) | Aggregate root + state transitions + invariants |
| [Domain/OrderLine.cs](../../OrderService/Domain/OrderLine.cs) | Line-item entity, owned by Order |
| [Domain/OrderStatus.cs](../../OrderService/Domain/OrderStatus.cs) | Enum: Placed / Paid / PaymentFailed / Shipped |
| [Domain/IOrderRepository.cs](../../OrderService/Domain/IOrderRepository.cs) | Repository port (write loaders + read projections) |
| [Domain/ICatalogClient.cs](../../OrderService/Domain/ICatalogClient.cs) | gRPC client port (substituted in tests) |
| [Domain/IEventPublisher.cs](../../OrderService/Domain/IEventPublisher.cs) | Event publish port (Wolverine implementation) |
| [Infrastructure/OrderRepository.cs](../../OrderService/Infrastructure/OrderRepository.cs) | EF Core repository (write loaders + read projections) |
| [Infrastructure/GrpcCatalogClient.cs](../../OrderService/Infrastructure/GrpcCatalogClient.cs) | gRPC adapter to CatalogService |
| [Infrastructure/WolverineEventPublisher.cs](../../OrderService/Infrastructure/WolverineEventPublisher.cs) | Wolverine `IMessageBus.PublishAsync` adapter |
| [Infrastructure/Data/OrderDbContext.cs](../../OrderService/Infrastructure/Data/OrderDbContext.cs) | EF Core context; SQL Server `RowVersion` concurrency token |
| [Program.cs](../../OrderService/Program.cs) | Composition root: Wolverine + EF + auth + transports |

---

## See also

- [docs/architecture.md](../architecture.md) — system-level view
- [docs/cqrs-data-access.md](../cqrs-data-access.md) — read/write split rule
- [docs/transactional-outbox.svg](../transactional-outbox.svg) — outbox mechanics diagram
- [docs/event-catalog.md](../event-catalog.md) — every event's shape and producer/consumer
