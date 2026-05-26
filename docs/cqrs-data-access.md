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

## Canonical shape — DbContext directly, no repository wrapper

All five services share one shape: handlers take `DbContext` directly (no `I*Repository` /
`I*ReadStore` wrapper). The read and write paths still split, but the split is enforced by
code-shape discipline inside the handler instead of by separate interface methods.

### Read path — project to DTO inline

```csharp
// OrderService/Features/GetOrderById.cs
public class GetOrderByIdHandler(OrderDbContext context)
{
    public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
        => context.Orders.AsNoTracking()
            .Where(o => o.Id == request.OrderId)
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
            .FirstOrDefaultAsync(cancellationToken);
}
```

The projection IS the read contract — there's nothing to wrap, nothing to mock. CatalogService
follows the exact same shape; `GetProductByIdHandler` projects to `ProductDto` inline (with
the `IProductCache.GetOrLoadAsync` factory wrapping the projection for cache-aside reads).

### Write path — load tracked, mutate, save

```csharp
// OrderService/Features/PaymentCompletedHandler.cs
public class PaymentCompletedHandler(OrderDbContext context)
{
    public async Task HandleAsync(PaymentCompletedEvent @event, CancellationToken cancellationToken)
    {
        var order = await context.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == @event.OrderId, cancellationToken);
        if (order is null) return;
        if (order.Status != OrderStatus.Placed) return; // idempotency guard

        order.MarkAsPaid();
        await context.SaveChangesAsync(cancellationToken);
    }
}
```

The aggregate is loaded tracked (no `AsNoTracking`), mutated via a named state-transition
method (`MarkAsPaid`, never `Status = Paid`), and persisted with `SaveChangesAsync`. Wolverine's
`AutoApplyTransactions` wraps the SaveChanges + outbox staging in one DB transaction.

### Why the read/write split is still enforced

Read paths never load tracked entities — `AsNoTracking().Select(DTO)` is the shape. Write
paths never project to DTO — they need the tracked aggregate so EF can detect changes on
SaveChanges. Mixing them silently breaks: an `AsNoTracking()` load followed by a mutation
would no-op on SaveChanges (no change-tracker entry exists), and a `Select()`-projected DTO
can't be mutated and persisted at all.

The split lives in the handler's code shape, not in separate interface methods. Code review
catches mixing via the architecture-reviewer agent's "When reviewing query handlers" / "When
reviewing write handlers" checklists.

---

## Handler inventory

### Query handlers (all return DTOs via projection)

| Service | Query | Handler | Data access | Returns |
|---------|-------|---------|-------------|---------|
| Catalog | `GetProductByIdQuery` | `GetProductByIdHandler` | `context.Products.AsNoTracking().Where(...).Select(ProductDto).FirstOrDefaultAsync` (wrapped in `IProductCache.GetOrLoadAsync`) | `ProductDto?` |
| Catalog | `GetAllProductsQuery` | `GetAllProductsHandler` | `context.Products.AsNoTracking().OrderBy(Id).Skip().Take().Select(ProductDto).ToListAsync` | `IReadOnlyList<ProductDto>` |
| Catalog | `SearchProductsQuery` | `SearchProductsHandler` | `context.Products.AsNoTracking().Where(EF.Functions.ILike).Select(ProductDto).ToListAsync` | `IReadOnlyList<ProductDto>` |
| Order | `GetOrderByIdQuery` | `GetOrderByIdHandler` | `context.Orders.AsNoTracking().Where(...).Select(OrderSummaryDto).FirstOrDefaultAsync` | `OrderSummaryDto?` |
| Order | `GetOrdersByBuyerQuery` | `GetOrdersByBuyerHandler` | `context.Orders.AsNoTracking().Where(...).OrderByDescending.Skip().Take().Select(OrderSummaryDto).ToListAsync` | `IReadOnlyList<OrderSummaryDto>` |
| Shipping | `GetShipmentByOrderQuery` | `GetShipmentByOrderHandler` | `context.Shipments.AsNoTracking().Where(...).Select(ShipmentDto).FirstOrDefaultAsync` | `ShipmentDto?` |

### Command handlers (load tracked entities, mutate, save)

| Service | Command | Handler | Side Effects |
|---------|---------|---------|--------------|
| Catalog | `CreateProductCommand` | `CreateProductHandler` | `context.Products.AddAsync` → `SaveChangesAsync` |
| Catalog | `UpdateProductCommand` | `UpdateProductHandler` | `context.Products.Include(Category).FirstOrDefaultAsync` → `UpdateDetails()` → `SaveChangesAsync` → `cache.InvalidateAsync` |
| Catalog | `ReserveStockCommand` | `ReserveStockHandler` | `context.Products.FirstOrDefaultAsync` → `AdjustStock()` → `SaveChangesAsync` → `cache.InvalidateAsync` |
| Order | `PlaceOrderCommand` | `PlaceOrderHandler` | gRPC validation → `AddAsync` → publish `OrderPlacedEvent` |
| Payment | `ProcessPaymentCommand` | `ProcessPaymentHandler` | `IPaymentRepository.GetByOrderIdAsync` → gateway → `AddAsync`/`UpdateAsync` → publish event |
| Shipping | `CreateShipmentCommand` | `CreateShipmentHandler` | `IShipmentRepository.GetByOrderIdAsync` (idempotency check) → `AddAsync` → publish `ShipmentDispatchedEvent` |

### Event/saga handlers (load tracked entities, mutate, save)

| Service | Event | Handler | Side Effects |
|---------|-------|---------|--------------|
| Order | `PaymentCompletedEvent` | `PaymentCompletedHandler` | `context.Orders.FirstOrDefaultAsync` → `MarkAsPaid()` → `SaveChangesAsync` |
| Order | `PaymentFailedEvent` | `PaymentFailedHandler` | `context.Orders.FirstOrDefaultAsync` → `MarkAsPaymentFailed()` → `SaveChangesAsync` |
| Order | `ShipmentDispatchedEvent` | `ShipmentDispatchedHandler` | `context.Orders.FirstOrDefaultAsync` → `MarkAsShipped()` → `SaveChangesAsync` |
| Payment | `OrderPlacedEvent` | `OrderPlacedHandler` | Invokes `ProcessPaymentCommand` |
| Shipping | `PaymentCompletedEvent` | `PaymentCompletedHandler` | Invokes `CreateShipmentCommand` |

---

## Caching interaction (CatalogService)

`GetProductByIdHandler` wraps the inline projection in `IProductCache.GetOrLoadAsync`. The cache stores **`ProductDto`** (the projection result), not the entity. On cache hit, no DB load happens; on cache miss, the factory runs the EF projection exactly once (HybridCache provides stampede protection).

Key property: **the entity never materializes on the read path.** The projection emits the DTO directly, the cache holds DTOs, the handler returns a DTO, the endpoint serializes a DTO.

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

1. **Query handlers project to DTO inline.** `context.Foos.AsNoTracking().Where(...).Select(new FooDto { ... }).ToListAsync(ct)` — no in-memory entity-to-DTO mapping.
2. **Command/event/saga handlers load tracked aggregates.** They mutate via aggregate methods (`MarkAsPaid`, never `Status = Paid`) and save through `SaveChangesAsync`.
3. **Code shape is the contract.** A handler that calls `AsNoTracking().Select(DTO)` is a read; a handler that loads tracked + mutates + saves is a write. No interface ambiguity to resolve.
4. **No N+1.** Read projections inline child collections via `.Select(...)`; tracked loads use `Include` for graph members the mutation actually touches.
5. **No repository wrapper.** `DbContext` IS Unit-of-Work, `DbSet<T>` IS Repository. Wrapping them in `I*Repository` adds layers without capability — and the only thing the wrapper enabled (handler-mocking in unit tests) has been replaced with integration tests against Testcontainers.
