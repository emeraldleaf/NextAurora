# CQRS & Data Access Strategy

> **Status:** This document was rewritten on 2026-05-24 to encode the **read/write method split** as a hard rule (was previously prescribed as "future cleanup"). The repo now demonstrates the pattern across every service. See [STATUS.md](STATUS.md) for the timeline.

## Overview

NextAurora implements CQRS (Command Query Responsibility Segregation) at the data-access layer, not just the application layer. Query handlers and command/saga handlers reach EF Core through **different repository methods** with different shapes:

- **Read methods** return DTOs directly. They project inside the IQueryable (`AsNoTracking().Select(...)`), so EF emits SQL for only the columns the DTO needs and skips entity materialization entirely.
- **Write methods** return tracked domain entities. They `Include` whatever the mutation touches, the caller mutates via aggregate methods, and `SaveChangesAsync` persists the change.

A single method serving **both** a query handler and a command handler is the canonical anti-pattern this strategy exists to remove. When the same `GetByIdAsync` was used to load an `Order` for both `GetOrderByIdHandler` (read, then map to DTO) and `PaymentCompletedHandler` (write, then mutate and save), the read path paid for entity materialization, parent-cartesian rows from `Include`, and an in-memory mapper pass — all wasted. The split fixes that by giving each path the method shape it actually needs.

---

## The hard rule

**Query handlers must depend on a DTO-returning data access method. Mapping in the handler is the anti-pattern.**

```csharp
// Anti-pattern: load entity, map in memory
var order = await repository.GetByIdAsync(id, ct);                   // full entity + Include + ToList
return order is null ? null : OrderSummaryMapper.ToDto(order);       // wasteful

// Pattern: repository projects in EF, returns DTO
return await repository.GetSummaryByIdAsync(id, ct);                 // SELECT only DTO columns
```

Why the anti-pattern is wasteful:

1. **Over-read.** SELECT emits every column on the entity (including write-only ones like `RowVersion`, audit timestamps) even though the DTO drops them.
2. **Parent-cartesian rows.** With a collection `Include` (e.g. `Order` → `OrderLines`), each parent row is duplicated once per child. 20 orders × 5 lines each = 100 result rows with the parent columns repeated 100 times.
3. **Double materialization.** Rows → entity tree → DTO. Each pass allocates.
4. **Bypasses query-shape optimizations.** EF can use grouped JSON / correlated subqueries / column pruning when projecting; it can't when materializing the entity.

The handler-level mapper is a *symptom*. The fix isn't a better mapper, it's removing the entity hop entirely by projecting in the IQueryable.

---

## Why projection kills cartesian rows (the EF mechanism)

The "cartesian rows" claim above deserves a precise mechanism, because "projection avoids cartesian rows" is true *operationally* but not *inherently* — there's a specific EF Core behavior doing the work, and knowing which behavior matters when you're optimizing or debugging a query.

### The two separate axes

Read-path queries have two independent concerns that the projection rule resolves at once:

1. **Object duplication on the client.** When EF materializes a parent + collection-Include into entity instances, the same parent appears once per cartesian row. Without identity resolution you get N duplicate parent objects; with `AsNoTrackingWithIdentityResolution()` EF maintains a per-query identity map and stitches the rows back into one parent with an N-item collection. **This fixes the object graph, not the SQL.**

2. **SQL row shape.** Whether the database actually returns cartesian-duplicated rows is determined by the query EF emits — which depends on whether you're materializing an entity graph or projecting to a DTO with a nested collection.

These are independent. Identity resolution does nothing to the SQL; auto-split-on-projection does nothing to client-side identity (because there are no entities to identity-track in a projection).

### What EF Core actually does per query shape

| Query shape | SQL | Client cost |
|---|---|---|
| `.AsNoTracking().Include(p => p.Children)` (materialize entities) | Single JOIN — parent columns repeated per child row | Parent materialized N times; without identity resolution = N duplicate parent objects |
| `.AsNoTrackingWithIdentityResolution().Include(p => p.Children)` (materialize entities) | **Same JOIN** — cartesian rows still come over the wire | Per-query identity map stitches rows back into one parent object |
| `.AsNoTracking().Include(p => p.Children).AsSplitQuery()` (materialize entities) | **Two queries** — parents, then children with `WHERE ParentId IN (...)`. No cartesian rows. | Loss of single-snapshot consistency between the two queries unless wrapped in an explicit transaction at a suitable isolation level |
| `.AsNoTracking().Select(p => new Dto { ..., Items = p.Children.Select(c => new ChildDto {...}).ToList() })` (project) | **EF auto-splits the projected collection** — separate query for the children, no JOIN, no cartesian rows | One materialization pass straight into DTOs. No entity hop, no identity-resolution question (no entities). |

So the operational rule "project to DTO" gets you the right SQL by triggering EF's automatic split behavior for projected collection navigations (EF Core 5+). The "no entity materialization" win is real but is a *separate* benefit (column savings + zero allocations of entity instances) from the cartesian-rows win.

### What does NOT trigger the auto-split

Be careful with projection shapes that look like projections but force a flattened JOIN:

```csharp
// Flat projection across the JOIN — cartesian rows STILL happen.
ctx.Parents.SelectMany(p => p.Children.Select(c => new { p.Name, c.X }));

// Same: projecting a collection-derived scalar in a way that forces flattening.
ctx.Parents.Select(p => new { p.Name, FirstChild = p.Children.First().X });
```

The auto-split fires specifically on projecting a **collection** (`p.Children.Select(...).ToList()` or `.ToArray()` inside a parent projection). When in doubt, `.ToQueryString()` the IQueryable in a debugger and look for one query versus two.

### Other ways to kill cartesian rows (when projection isn't enough)

Cases where you genuinely need an entity graph or your projection shape forces a JOIN:

1. **Invert the root.** If you really want "children with parent context," query the child as the root: `ctx.Children.Where(...).Select(c => new { c.X, c.Parent.Name })`. Parent columns repeat only as much as the data genuinely requires — no fan-out.
2. **Aggregate in SQL.** If you're including children only to count / sum / check existence, project the aggregate: `.Select(p => new { p.Id, Count = p.Children.Count() })`. The children never cross the wire.
3. **One collection per JOIN.** Cartesian explosion is multiplicative across *sibling* collections at the same level (`parent × childrenA × childrenB`). One collection in a JOIN is just denormalized repetition, not explosion. Two sibling collections is the real disaster — pull one separately.
4. **`AsSplitQuery()` for entity materialization.** The built-in lever when you must materialize and don't want to project. Pair with `AsNoTrackingWithIdentityResolution()`. Accept the consistency tradeoff between the split queries (or wrap in an explicit transaction).
5. **Manual two-query stitch.** When `AsSplitQuery`'s `WHERE id IN (...)` generation isn't what you want (e.g. you need to page or filter the children independently): run the parent query, then a second filtered child query, then join client-side. More code, no surprises.

For most read paths in this codebase, projection-to-DTO covers it. The other options are for the cases where the projection shape is forced by something else (a third-party API, an export, a graph traversal).

### Consistency caveat

Both split-query approaches (the auto-split-on-projection that the rule prescribes, and the manual `AsSplitQuery()`) execute multiple SQL statements in separate round trips. Without an explicit transaction at an isolation level that prevents non-repeatable reads, the parent and child queries see slightly different snapshots — a write between the two queries could surface as "parent's `OrderCount = 3` but only 2 child rows came back." For most read endpoints this is fine; for read-modify-write sequences or sagas that depend on consistent snapshots, wrap in a transaction. This is a general concern for any multi-statement read, not unique to the projection rule.

---

## Canonical shape per architecture style

Two service shapes live in this repo (VSA for Order/Shipping/Payment/Notification, Clean Architecture for CatalogService). The rule applies to both; the *shape* differs because the layer constraints differ.

### VSA services (Order, Shipping, Payment, Notification)

The repository interface lives in `ServiceName/Domain/IFooRepository.cs`, in the same csproj as `Features/` and `Infrastructure/`. There's no separate Domain project, so the interface can reference `NextAurora.Contracts.DTOs` without breaking a layer rule.

**Pattern:** add sibling DTO-returning methods to the existing repository interface.

```csharp
// OrderService/Domain/IOrderRepository.cs
public interface IOrderRepository
{
    // Write path — loaded tracked, mutated, saved
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);

    // Read paths — projected in EF, returns DTO
    Task<OrderSummaryDto?> GetSummaryByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<OrderSummaryDto>> GetSummariesByBuyerIdAsync(
        Guid buyerId, int page, int pageSize, CancellationToken ct = default);

    Task AddAsync(Order order, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
}
```

```csharp
// OrderService/Infrastructure/OrderRepository.cs
public async Task<OrderSummaryDto?> GetSummaryByIdAsync(Guid id, CancellationToken ct = default)
    => await context.Orders.AsNoTracking()
        .Where(o => o.Id == id)
        .Select(o => new OrderSummaryDto
        {
            OrderId = o.Id,
            BuyerId = o.BuyerId,
            Status = o.Status.ToString(),
            TotalAmount = o.TotalAmount,
            Currency = o.Currency,
            PlacedAt = o.PlacedAt,
            Lines = o.Lines.Select(l => new OrderLineSummaryDto
            {
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        })
        .FirstOrDefaultAsync(ct);
```

The query handler becomes a one-liner:

```csharp
public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
    => repository.GetSummaryByIdAsync(request.OrderId, cancellationToken);
```

The write/saga handlers keep using `GetByIdAsync` and mutating the loaded aggregate. Both paths coexist on the same interface; tests substitute the same interface either way.

### Clean Architecture (CatalogService)

`IProductRepository` lives in `CatalogService.Domain/Interfaces/`. The Domain project does **not** reference `NextAurora.Contracts` — that's the layer rule. Adding a DTO-returning method to `IProductRepository` would force the Domain project to take a Contracts dependency, violating the layer rule.

**Pattern:** introduce a sibling read-store interface in the Application layer, implementation in Infrastructure.

```csharp
// CatalogService.Application/Interfaces/IProductReadStore.cs
public interface IProductReadStore
{
    Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDto>> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int page, int pageSize, CancellationToken ct = default);
}
```

```csharp
// CatalogService.Infrastructure/Repositories/ProductReadStore.cs
public class ProductReadStore(CatalogDbContext context) : IProductReadStore
{
    public async Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Products.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                /* ... */
                Category = p.Category != null ? p.Category.Name : ""
            })
            .FirstOrDefaultAsync(ct);
    /* etc. */
}
```

Query handlers depend on `IProductReadStore`. `UpdateProductHandler` and `ReserveStockHandler` keep depending on `IProductRepository` (Domain interface) — they need the tracked aggregate.

This split is the textbook CQRS data-access pattern. Two interfaces, two implementations, no layer violations.

---

## Handler inventory

### Query handlers (all return DTOs via projection)

| Service | Query | Handler | Data access | Returns |
|---------|-------|---------|-------------|---------|
| Catalog | `GetProductByIdQuery` | `GetProductByIdHandler` | `IProductReadStore.GetByIdAsync` (cached via `IProductCache`) | `ProductDto?` |
| Catalog | `GetAllProductsQuery` | `GetAllProductsHandler` | `IProductReadStore.GetAllAsync` | `IReadOnlyList<ProductDto>` |
| Catalog | `SearchProductsQuery` | `SearchProductsHandler` | `IProductReadStore.SearchAsync` | `IReadOnlyList<ProductDto>` |
| Order | `GetOrderByIdQuery` | `GetOrderByIdHandler` | `IOrderRepository.GetSummaryByIdAsync` | `OrderSummaryDto?` |
| Order | `GetOrdersByBuyerQuery` | `GetOrdersByBuyerHandler` | `IOrderRepository.GetSummariesByBuyerIdAsync` | `IReadOnlyList<OrderSummaryDto>` |
| Shipping | `GetShipmentByOrderQuery` | `GetShipmentByOrderHandler` | `IShipmentRepository.GetSummaryByOrderIdAsync` | `ShipmentDto?` |

### Command handlers (load tracked entities, mutate, save)

| Service | Command | Handler | Side Effects |
|---------|---------|---------|--------------|
| Catalog | `CreateProductCommand` | `CreateProductHandler` | `AddAsync` |
| Catalog | `UpdateProductCommand` | `UpdateProductHandler` | `IProductRepository.GetByIdAsync` → mutate → `UpdateAsync` |
| Catalog | `ReserveStockCommand` | `ReserveStockHandler` | `IProductRepository.GetByIdAsync` → mutate → `UpdateAsync` |
| Order | `PlaceOrderCommand` | `PlaceOrderHandler` | gRPC validation → `AddAsync` → publish `OrderPlacedEvent` |
| Payment | `ProcessPaymentCommand` | `ProcessPaymentHandler` | `IPaymentRepository.GetByOrderIdAsync` → gateway → `AddAsync`/`UpdateAsync` → publish event |
| Shipping | `CreateShipmentCommand` | `CreateShipmentHandler` | `IShipmentRepository.GetByOrderIdAsync` (idempotency check) → `AddAsync` → publish `ShipmentDispatchedEvent` |

### Event/saga handlers (load tracked entities, mutate, save)

| Service | Event | Handler | Side Effects |
|---------|-------|---------|--------------|
| Order | `PaymentCompletedEvent` | `PaymentCompletedHandler` | `IOrderRepository.GetByIdAsync` → `MarkAsPaid()` → `UpdateAsync` |
| Order | `PaymentFailedEvent` | `PaymentFailedHandler` | `IOrderRepository.GetByIdAsync` → `MarkAsPaymentFailed()` → `UpdateAsync` |
| Order | `ShipmentDispatchedEvent` | `ShipmentDispatchedHandler` | `IOrderRepository.GetByIdAsync` → `MarkAsShipped()` → `UpdateAsync` |
| Payment | `OrderPlacedEvent` | `OrderPlacedHandler` | Invokes `ProcessPaymentCommand` |
| Shipping | `PaymentCompletedEvent` | `PaymentCompletedHandler` | Invokes `CreateShipmentCommand` |

---

## Caching interaction (CatalogService)

`GetProductByIdHandler` wraps the read store call in `IProductCache.GetOrLoadAsync`. The cache stores **`ProductDto`** (the projection result), not the entity. On cache hit, no DB load happens; on cache miss, the factory invokes `IProductReadStore.GetByIdAsync` exactly once (HybridCache provides stampede protection).

Key property: **the entity never leaves the Infrastructure layer for the read path.** The cache holds DTOs, the handler receives a DTO, the endpoint serializes a DTO. Projection-in-EF preserves this invariant.

---

## Why this is a hard rule, not a guideline

The old framing — "AsNoTracking is applied selectively; shared methods preserve tracking deliberately" — was a pragmatic concession that wasn't actually carrying its weight. It saved one repository method per service at the cost of:

- Every read paying for full entity materialization
- Every read shipping cartesian-exploded rows over the wire when `Include` was in play
- Every handler running an in-memory mapper pass
- The reader of the code having to know which `GetByIdAsync` they were looking at without the signature telling them

The split is a few extra lines per service. Once it's in place, the *signature itself* tells you the intent: a method returning a DTO is a read; a method returning an entity is a write loader. No comments needed.

---

## Key principles

1. **Query handlers depend on DTO-returning data access methods.** No in-memory mapping from entity to DTO.
2. **Command/event/saga handlers depend on entity-returning loader methods.** They mutate the aggregate and save through it.
3. **Repository method shape is the contract.** Returning `Product?` means write path. Returning `ProductDto?` means read path. The caller doesn't have to read documentation to know which they got.
4. **No N+1.** Read projections inline child collections via `.Select(...)`; entity loaders use `Include` for graph members the mutation actually touches.
5. **Layer dependencies stay intact.** In Clean Architecture (CatalogService), DTO-returning methods live on a separate interface in the Application layer (`IProductReadStore`), not on the Domain-layer `IProductRepository`.
