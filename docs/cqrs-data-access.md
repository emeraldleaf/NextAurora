# CQRS & Data Access Strategy

## Overview

NextAurora implements CQRS (Command Query Responsibility Segregation) at the application layer. Commands and queries are separate record types with dedicated Wolverine handler POCOs. Query handlers return DTOs and never modify state. Command handlers mutate domain entities and publish events.

---

## CQRS Separation

### Query Path

```
HTTP Request → Endpoint → IMessageBus.InvokeAsync<TResult>(query)
  → QueryHandler.Handle() → Repository (read-only) → Domain Entity → DTO
```

All query handlers map domain entities to DTOs before returning. They never call `SaveChangesAsync()`, publish events, or modify entity state.

| Service | Query | Handler | Returns |
|---------|-------|---------|---------|
| Catalog | `GetProductByIdQuery` | `GetProductByIdHandler` | `ProductDto?` |
| Catalog | `GetAllProductsQuery` | `GetAllProductsHandler` | `IReadOnlyList<ProductDto>` |
| Catalog | `SearchProductsQuery` | `SearchProductsHandler` | `IReadOnlyList<ProductDto>` |
| Order | `GetOrderByIdQuery` | `GetOrderByIdHandler` | `OrderSummaryDto?` |
| Order | `GetOrdersByBuyerQuery` | `GetOrdersByBuyerHandler` | `IReadOnlyList<OrderSummaryDto>` |
| Shipping | `GetShipmentByOrderQuery` | `GetShipmentByOrderHandler` | `ShipmentDto?` |

### Command Path

```
HTTP Request → Endpoint → IMessageBus.InvokeAsync<TResult>(command)
  → CommandHandler.Handle() → Repository (read + write) → Domain Entity → Event Published
```

Command handlers create or mutate entities, persist changes via `SaveChangesAsync()`, and publish domain events through `IEventPublisher`.

| Service | Command | Handler | Side Effects |
|---------|---------|---------|-------------|
| Catalog | `CreateProductCommand` | `CreateProductHandler` | `AddAsync` |
| Catalog | `UpdateProductCommand` | `UpdateProductHandler` | `GetByIdAsync` → mutate → `UpdateAsync` |
| Catalog | `ReserveStockCommand` | `ReserveStockHandler` | `GetByIdAsync` → mutate → `UpdateAsync` |
| Order | `PlaceOrderCommand` | `PlaceOrderHandler` | gRPC validation → `AddAsync` → publish `OrderPlacedEvent` |
| Payment | `ProcessPaymentCommand` | `ProcessPaymentHandler` | `GetByOrderIdAsync` → gateway → `AddAsync`/`UpdateAsync` → publish event |
| Shipping | `CreateShipmentCommand` | `CreateShipmentHandler` | `GetByOrderIdAsync` → `AddAsync` → publish `ShipmentDispatchedEvent` |

### Event Handlers

Event handlers (triggered by Service Bus messages) follow the command path — they read an entity, mutate its state, and save:

| Service | Event | Handler | Side Effects |
|---------|-------|---------|-------------|
| Order | `PaymentCompletedEvent` | `PaymentCompletedHandler` | `GetByIdAsync` → `MarkAsPaid()` → `UpdateAsync` |
| Order | `PaymentFailedEvent` | `PaymentFailedHandler` | `GetByIdAsync` → `MarkAsPaymentFailed()` → `UpdateAsync` |
| Order | `ShipmentDispatchedEvent` | `ShipmentDispatchedHandler` | `GetByIdAsync` → `MarkAsShipped()` → `UpdateAsync` |
| Payment | `OrderPlacedEvent` | `OrderPlacedHandler` | Invokes `ProcessPaymentCommand` |
| Shipping | `PaymentCompletedEvent` | `PaymentCompletedHandler` | Invokes `CreateShipmentCommand` |

---

## EF Core Change Tracking Strategy

### Problem: Shared Repository Methods

The read and write paths share the same repository interfaces. Several `GetByIdAsync` methods are called by both query handlers (read-only, don't need tracking) and command/event handlers (need tracking for subsequent `Update` calls).

Adding `AsNoTracking()` to shared methods would break command handlers — EF Core wouldn't detect changes on the returned entity, and `SaveChangesAsync()` would silently skip the update.

### Solution: Selective AsNoTracking

Methods are categorized as **read-only** (only called from query handlers) or **shared** (called from both query and command/event handlers). `AsNoTracking()` is applied only to read-only methods.

#### Read-Only Methods (AsNoTracking applied)

These methods are exclusively called from query handlers that map to DTOs:

| Repository | Method | Callers |
|-----------|--------|---------|
| `ProductRepository` | `GetAllAsync` | `GetAllProductsHandler` |
| `ProductRepository` | `GetByCategoryAsync` | Endpoint only |
| `ProductRepository` | `SearchAsync` | `SearchProductsHandler` |
| `CategoryRepository` | `GetByIdAsync` | No command/event callers |
| `CategoryRepository` | `GetAllAsync` | No command/event callers |
| `OrderRepository` | `GetByBuyerIdAsync` | `GetOrdersByBuyerHandler` |

#### Shared Methods (Tracking preserved)

These methods are called by command or event handlers that mutate and save the entity:

| Repository | Method | Read Callers | Write Callers |
|-----------|--------|-------------|--------------|
| `ProductRepository` | `GetByIdAsync` | `GetProductByIdHandler` | `UpdateProductHandler`, `ReserveStockHandler` |
| `OrderRepository` | `GetByIdAsync` | `GetOrderByIdHandler` | `PaymentCompletedHandler`, `PaymentFailedHandler`, `ShipmentDispatchedHandler` |
| `PaymentRepository` | `GetByOrderIdAsync` | — | `ProcessPaymentHandler` |
| `ShipmentRepository` | `GetByOrderIdAsync` | `GetShipmentByOrderHandler` | `CreateShipmentHandler` |

### Why Not AsNoTracking Everywhere?

When `AsNoTracking()` is applied to a query, EF Core does not add the returned entity to the change tracker. If a handler then modifies the entity and calls `context.Update(entity)`, EF Core must reattach it — adding unnecessary complexity and risk. Worse, if `Update()` is not called explicitly, the changes are silently lost.

Keeping tracking enabled on shared methods ensures the read-then-mutate-then-save pattern works correctly without additional plumbing.

### Future: Read/Write Repository Separation

Per the project's Interface Segregation principle (CLAUDE.md), the shared methods could be split into separate read-only and read-write repository interfaces:

```csharp
// Read-only — used by query handlers
public interface IProductReadRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken ct = default);
}

// Read-write — used by command/event handlers
public interface IProductRepository : IProductReadRepository
{
    Task AddAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
}
```

The read-only implementation would apply `AsNoTracking()` globally. This is not currently implemented because the shared `GetByIdAsync` methods need tracking for the command path, and splitting them requires wiring two implementations per service.

---

## Key Principles

1. **Query handlers never modify state.** They return DTOs, never domain entities.
2. **Command and event handlers own mutations.** They read entities with tracking, mutate via domain methods, and persist via `SaveChangesAsync()`.
3. **AsNoTracking is applied selectively.** Only on methods proven to be exclusively read-only.
4. **No N+1 queries.** All collection loads use `Include()` for navigation properties, never loop-and-query patterns.
5. **Repositories return domain entities, not DTOs.** The mapping to DTOs happens in handlers, keeping the infrastructure layer unaware of application concerns.
