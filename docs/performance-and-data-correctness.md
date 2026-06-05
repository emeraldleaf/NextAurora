# Performance & Data Correctness

This is the low-level guide to NextAurora's performance and data-correctness rules — what we enforce, why, where it actually applies in the codebase, and what's still missing. It's written to be read end-to-end so you can discuss the decisions, point at the specifications they enforce, and explain the reasoning behind each one.

The hard rules live in [CLAUDE.md](../CLAUDE.md#performance-rules). The deeper "how" lives in [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md). This doc is the "why."

## Table of Contents

- [Philosophy](#philosophy)
- [The 14 always-on rules](#the-14-always-on-rules)
- [Decision: optimistic concurrency tokens](#decision-optimistic-concurrency-tokens)
- [Decision: read/write method split (CQRS data access)](#decision-readwrite-method-split-cqrs-data-access)
- [Decision: distributed read caching with HybridCache](#decision-distributed-read-caching-with-hybridcache)
- [Decision: when to reach past EF Core (Dapper escape hatch)](#decision-when-to-reach-past-ef-core-dapper-escape-hatch)
- [Concurrency hazards: what the build enforces](#concurrency-hazards-what-the-build-enforces)
- [Resolved: transactional outbox via Wolverine](#resolved-transactional-outbox-via-wolverine)
- [Resolved: concurrency exception handling](#resolved-concurrency-exception-handling)
- [Resolved: migration tooling wired up](#resolved-migration-tooling-wired-up)
- [Resolved: orphaned EventLogs / replay endpoints deleted](#resolved-orphaned-eventlogs--replay-endpoints-deleted)
- [Remaining cleanup](#remaining-cleanup)
- [Specifications cross-reference](#specifications-cross-reference)
- [What changed when](#what-changed-when)

---

## Philosophy

Four principles drive every rule in this doc:

1. **Measure before optimizing.** Most "optimizations" applied without a profiler hurt more than they help. Examples worth knowing:
   - `AsNoTracking()` blanket-applied with `Include` duplicates shared related entities (Customer fetched once, materialized 500 times) — see [decision: AsNoTracking strategy](#decision-readwrite-method-split-cqrs-data-access).
   - `AsSplitQuery()` shifts work from DB to app server and may make things worse.
   - Compiled queries optimize the cheapest part of the pipeline.

2. **Correctness > performance.** Concurrency tokens, transactional outbox, and connection-string isolation aren't perf concerns — they're correctness concerns that perf shortcuts often break. A faster-but-occasionally-wrong system is worse than a correct one with known bottlenecks.

3. **Make the right thing easy.** Hard rules in CLAUDE.md exist because we want every new query to use projection by default, every new endpoint to paginate by default, every new aggregate to have a concurrency token by default. The skill has the deeper material; CLAUDE.md is the on-ramp.

4. **Use the platform — don't reinvent it.** Modern .NET ships well-engineered primitives for the patterns we'd otherwise hand-roll: `HybridCache` for two-tier caching with stampede protection, Wolverine's transactional outbox for the dual-write problem, `IDbContextFactory<T>` for parallel EF work, `Microsoft.AspNetCore.OpenApi` for spec emission, source-generated `System.Text.Json` for AOT-friendly serialization, `Asp.Versioning.Http` for URL-segment versioning. Bespoke versions of the same thing tend to ship the easy 80% and miss the load-bearing 20% — locking, dedup, atomic invalidation, async-safe coordination. **Default to the framework primitive; document the case if you reach past it.** This shows up concretely in the [outbox](#resolved-transactional-outbox-via-wolverine) and [caching](#decision-distributed-read-caching-with-hybridcache) decisions below.

---

## The 14 always-on rules

These are in [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules). Below is each rule with the *why* and *where in the code it applies*.

### 1. EF Core reads use `AsNoTracking()` + projection

**Why:** Two compounding wins. Tracking has per-row cost (change detection metadata, identity map maintenance) that's wasted on read paths. Projection (`.Select(... new DTO ...)`) means EF generates SQL with only the columns we need, materializes flat DTOs (no entity graph), and skips identity-resolution overhead entirely.

**Where it applies:** All query handlers in `*/Application/Handlers/*QueryHandler.cs` (or the equivalent). Returns `*Dto` types, never domain entities.

**Edge case — entity graphs without tracking:** If you genuinely need a populated entity graph (rare; almost always projection wins), don't use plain `AsNoTracking().Include(...)` — that *removes* identity resolution and creates duplicate related entities (every order's Customer is a separate object even when 500 orders share one customer). Use `AsNoTrackingWithIdentityResolution()` for that case.

### 2. No N+1 — use `Include` or projection

**Why:** A query inside a `foreach` over another query result generates 1+N round trips. At 500 orders with one DB hit per order, that's 501 queries instead of 1.

**Where it applies:** Anywhere you're iterating results. The OrderService `PlaceOrderHandler` foreach loop is *not* an N+1 — it's calling a gRPC service per line, not the DB. That's a different concern (per-line gRPC fan-out) but isn't covered by this rule.

### 3. Async on request paths — `await` everywhere, propagate `CancellationToken`

**Why:** `.Result` / `.Wait()` blocks a thread waiting for an async result, which under load causes thread-pool starvation and (in some hosting models) deadlocks. `CancellationToken` propagation means timeouts and client-disconnects actually stop work, instead of leaking compute on requests no one is listening for.

**Where it applies:** Every handler, repository method, endpoint method, and middleware. The audit found 13 endpoint methods missing the parameter — Minimal API endpoints can read `HttpContext.RequestAborted`, so it's recoverable, but it's a code-review smell and worth a sweep.

### 4. Pagination — every list endpoint paginates with a server-side size cap

**Why:** Without a server-side cap, a malicious or buggy caller can request `size=1000000` and OOM the service. Without pagination at all, "this works for me" with 50 rows breaks at 50,000.

**Spec:** Every `GET` returning a collection must accept `page`/`pageSize` (or `cursor`) parameters. Server caps `pageSize` at ≤ 100. For large offsets (`Skip(100000)`), use **keyset pagination** (`WHERE id > lastSeenId`) — `OFFSET` is O(N) on the database.

**Where it applies:** `GET /api/v1/products`, `GET /api/v1/products/search` (Catalog), `GET /api/v1/orders/buyer/{buyerId}` (Order). All three take `?page=&pageSize=` with the size cap clamped server-side via the `ClampPaging` helper in each endpoint file. Repository methods carry `(int page, int pageSize)` parameters and apply `OrderBy + Skip + Take` for stable pagination.

### 5. Bulk ops use `ExecuteUpdateAsync` / `ExecuteDeleteAsync`

**Why:** Loading 10,000 rows just to flip `IsDiscounted = true` is 10,000 EF entities materialized, 10,000 change-tracker entries, 10,000 SQL UPDATEs at `SaveChanges`. `ExecuteUpdateAsync` translates to one SQL `UPDATE ... SET ... WHERE ...`. Difference is 100x to 1000x at scale.

**Caveat:** runs outside the change tracker. Don't mix with `SaveChanges` on the same entities in the same unit of work — you'll get stale tracked data.

**Where it applies:** Outbox cleanup (delete published rows older than X), bulk status flips (e.g. soft-delete sweeps), backfills. Not currently in use anywhere; relevant once the outbox is fixed (see [open issue](#open-issue-the-outbox-is-broken)).

### 6. Optimistic concurrency tokens

See [decision: optimistic concurrency tokens](#decision-optimistic-concurrency-tokens) for the full story.

### 7. Outbox atomicity

**Why:** Without atomicity, "save the order" and "publish the OrderPlacedEvent" are two operations that can fail independently. If the order is committed but the event publish crashes, downstream services never know about the order — payment never runs, the buyer is charged silently or never charged. This is the **dual-write problem** and it's the reason the outbox pattern exists.

**Spec (per CLAUDE.md):** the entity write and outbox-row write commit in the same transaction. Either both happen or neither does.

**Where it applies:** All three command handlers that publish events ([PlaceOrderHandler](../OrderService/Features/PlaceOrder.cs), [ProcessPaymentHandler](../PaymentService/Features/ProcessPayment.cs), [CreateShipmentHandler](../ShippingService/Features/CreateShipment.cs)). Implemented via Wolverine's transactional outbox — see [resolved: transactional outbox via Wolverine](#resolved-transactional-outbox-via-wolverine).

### 8. `DbContext` is not thread-safe

**Why:** EF's `DbContext` holds a connection, a change tracker, a query cache. Two parallel queries on one context race on all three — symptoms range from `InvalidOperationException` to silent state corruption.

**Spec:** Parallel queries (`Task.WhenAll(...)`) require `IDbContextFactory<T>` — one context per task. The scoped per-request context handles only sequential work.

**Where it applies:** None of our current handlers do parallel queries (which is why the audit found no violations). It matters once a handler legitimately fans out (e.g., a dashboard query loading orders, payments, shipments in parallel for one user). Don't introduce that without `IDbContextFactory<T>`.

### 9. Structured logging — message templates, not concatenation

**Why:** Two reasons.
- **Performance:** `$"User {user.Name} logged in"` allocates the string *even if logging is disabled*. The template form `"User {UserName} logged in"` skips allocation when the level is filtered out.
- **Observability:** Templates produce structured fields (`UserName=joe`) that are queryable in OTLP backends. Concatenated strings are opaque blobs.

**Where it applies:** Already universal in the codebase (audit found zero violations). The reason it matters here: the correlation/user/session scope from [docs/context-propagation.md](context-propagation.md) attaches structured fields via `logger.BeginScope()` — that machinery only works if log calls also use templates.

### 10. No logging in tight loops

**Why:** Per-item logs at hot-path frequency flood log ingestion and stall the request. A 10ms log call inside a loop over 1000 items is a 10-second tax on the request.

**Spec:** Log summaries (`"Processed {Count} items"`), not per-item lines.

### 11. DB connection hold time

**Why:** Connections are pooled; each open connection holds a slot. If a handler opens a connection, runs a query, then `await`s a 200ms HTTP call before disposing, that connection slot is held for 200ms doing nothing. At even modest concurrency, the pool exhausts → new requests time out on `OpenAsync`.

**Spec:** open → query → dispose, then do unrelated awaits. EF's `DbContext` enforces this implicitly when scoped per request and used sequentially. Manual `DbConnection` use (Dapper paths) needs explicit attention.

### 12. Cache invalidation in the write path

**Why:** Cache TTL eventually catches stale data, but "eventually" can be hours. If the handler that mutates an entity doesn't also invalidate the cache, every reader sees the stale value until the TTL expires. The window between commit and TTL is the bug surface.

**Spec:** the same handler that owns the change owns the invalidation. For domain events that affect cached entities cross-service (e.g., `ProductPriceChanged` invalidating product cache), the event handler invalidates.

**Where it applies:** [CatalogService.Domain.IProductCache](../CatalogService/Domain/IProductCache.cs), backed by `HybridCache` ([HybridProductCache.cs](../CatalogService/Infrastructure/Caching/HybridProductCache.cs)). [GetProductByIdHandler](../CatalogService/Features/GetProductById.cs) reads through it; [UpdateProductHandler](../CatalogService/Features/UpdateProduct.cs) and [ReserveStockHandler](../CatalogService/Features/ReserveStock.cs) call `InvalidateAsync` after their save in the same unit of work. Tag-based invalidation clears L1 (in-process) and L2 (Redis) atomically. Full rationale: [decision: distributed read caching with HybridCache](#decision-distributed-read-caching-with-hybridcache).

### 13. Migrations are immutable once applied

**Why:** Editing an applied migration changes history. If you edit a migration that's already been run in dev, the model snapshot drifts from the migration history, and the next migration generation will produce broken output. Worse, if you edit a migration that's been run in production, deploying the edited version either no-ops (already applied) or corrupts state.

**Spec:** Once a migration runs *anywhere*, it's frozen. Need a fix? New migration. Destructive changes (drop column/table, rename, NOT NULL on existing column) need a multi-step plan: deploy code that no longer reads the column → wait one release → drop the column in a follow-up migration.

**Where it applies:** Currently nowhere — migration tooling isn't set up yet. See [open issue: migration tooling](#open-issue-migration-tooling-not-wired-up).

### 14. Measure before optimizing

**Why:** Repeated theme. Stated as a hard rule because the temptation to add caching, compiled queries, `ValueTask`, `AsSplitQuery()` based on "this might be slow" is constant. Almost all of those are net-negative without a profiler showing the original is the bottleneck.

**Tools by use case:**
- **Code-level (which is faster, A or B?):** BenchmarkDotNet. Gives ns/op, allocations/op, GC stats.
- **System-level (where is the bottleneck?):** `dotnet-counters`, `dotnet-trace`, k6 / Azure Load Testing for traffic generation.
- **EF-specific (what SQL did this LINQ produce?):** `query.ToQueryString()` or `appsettings.Development.json` `"Microsoft.EntityFrameworkCore.Database.Command": "Information"`.

---

## Additional always-on patterns

These extend the 14 rules above with patterns that don't fit cleanly into a single rule shape. Same enforcement bar.

### Non-sargable predicates defeat indexes — fix at write time

A `Where(...)` that wraps the column in a function (`u.Email.ToLower() == x`, `o.CreatedAt.Date == today`) can't use a B-tree index on that column even if one exists — the planner falls back to a full scan.

**The fix is at write time, not at read time:**
- Normalize on insert/update (e.g. an `EmailNormalized` column populated by the aggregate factory + projected to in `Where(u => u.EmailNormalized == emailNormalized)`)
- Or use a case-insensitive collation at the column level

**Leading-wildcard substring search (`LIKE '%text%'`, `EF.Functions.ILike(p.Name, "%text%")`) isn't B-tree-indexable in any database** — escalate to Postgres `tsvector` full-text search or a dedicated search engine (Elasticsearch / OpenSearch / Meilisearch) when load justifies it. Reference: [`CatalogService/Features/SearchProducts.cs`](../CatalogService/Features/SearchProducts.cs) documents the leading-wildcard trade-off explicitly (intentional; full-text is the named next step if it becomes a bottleneck).

**Deeper principle:** indexes carry a write cost — every insert/update touches every index on the table — so an index the planner can't use is pure overhead, not free defense-in-depth. Adding more indexes isn't a universal speed-up; treat the index list like an interface — each one earns its keep against a real query.

### Parallelize independent awaits with `Task.WhenAll`

Sequential `await`s serialize latency for free. Async makes a single wait non-blocking; it does not make a *sequence* of waits cheap. When a handler makes N independent I/O calls — N gRPC requests to different services, N HTTP calls to different external APIs, N queries against *different* DbContexts (one per service) — sequential `await`s pay the sum of all latencies, while `Task.WhenAll` pays the max.

**Anti-shape:**
```csharp
var user = await GetUserAsync(id, ct);
var orders = await GetOrdersAsync(otherId, ct);      // doesn't depend on user
var notifications = await GetNotificationsAsync(ct); // doesn't depend on either
```

**Right shape:** kick all three off, await once, project the results.

**Reference shape:** [`OrderService/Features/PlaceOrder.cs:93`](../OrderService/Features/PlaceOrder.cs) — `Task.WhenAll(request.Lines.Select(line => catalogClient.GetProductAsync(line.ProductId, ct)))` is the canonical gRPC fan-out over independent line items, parallelism over the wire, no shared mutable state. The file documents the DbContext safety caveat at lines 89–92 explicitly.

**Don't parallelize:**
- (a) **Dependent operations** where the output of one feeds the input of another (`var user = await ...; var orders = await GetByUserId(user.Id, ct);`)
- (b) **Operations sharing the same EF `DbContext` scope** — DbContext is NOT thread-safe and parallel EF queries against the same context throw or corrupt state. Use `IDbContextFactory<T>` to mint one context per task (see rule 8 above)
- (c) **Operations whose failures must be observed independently** — `Task.WhenAll` surfaces only the first exception; the rest run to completion but get swallowed. Use `Task.WhenAll(...)` followed by inspection of each task's `.Exception` (or `Task.WhenEach` in .NET 10) when multi-failure surfacing matters

### Long-running work belongs on the message bus, not the synchronous HTTP handler

If a write path would take more than ~1s (multi-step external API chain, aggregation over thousands of rows, bulk import, report generation), reshape the endpoint as **202 Accepted**: validate + persist a tracking row + publish a Wolverine message + return `202` with the polling key in the body and a `Location` header pointing to a status endpoint. A background handler does the actual work; the client polls (`GET /jobs/{id}`, `GET /orders/{id}`, etc.) or receives a push (SignalR / SSE / email when the job completes).

**What counts as "the tracking row":** the aggregate being created can BE the tracking row when its ID is the polling key — that's the `POST /api/v1/orders` shape (the `Order` row IS the tracking record; `GET /api/v1/orders/{id}` IS the polling endpoint). A separate jobs table is only needed when there's no natural aggregate (bulk import of CSV with no per-row entity, report generation with no persistent output).

**The synchronous parts commit atomically two ways:**
- (a) when the endpoint dispatches via `bus.InvokeAsync<T>(command, ct)`, `AutoApplyTransactions` wraps the handler — `SaveChanges` flushes the entity write and Wolverine's staged envelope in one DB transaction
- (b) when the endpoint persists + publishes inline (no handler dispatch), use the `BeginTransactionAsync` → work + `PublishAsync` → `SaveChangesAsync` → `CommitAsync` wrap from the Outbox-outside-handler trap (see [observability-and-context-propagation.md "Outbox outside a Wolverine handler"](observability-and-context-propagation.md#outbox-outside-a-wolverine-handler--atomicity-trap)). Skipping `SaveChangesAsync` after `PublishAsync` and before `Commit` silently drops the staged envelope

**Why it matters:** the HTTP request holds a thread, a DB connection, and a concurrency-budget slot for the full duration of the handler — a small spike on a slow endpoint can starve the rest of the API. Response time and work duration are different things; the rule is to keep response time bounded.

NextAurora already has the full machinery — Wolverine + Service Bus + outbox + saga handlers — so the rule is "use it when a handler would otherwise block." The current `POST /api/v1/orders` is the canonical reference: the handler validates + persists the `Order` + stages `OrderPlacedEvent` + returns `OrderId`, then PaymentService + ShippingService handle downstream work async via the saga. Note that `bus.InvokeAsync<Guid>` awaits the *Place Order handler* synchronously — what makes this the right shape isn't the response code, it's that the handler only does validate-persist-stage and stays sub-second; the minutes-scale work is downstream consumers of the staged event.

**Same rule for Wolverine handlers themselves**: a handler body that runs for minutes is the same anti-pattern with a different colored connection — break the work into a follow-up message handler.

**Cloud-managed alternatives** when the worker pool needs scale-to-zero or a multi-step durable workflow:
- AWS SQS + Lambda or Azure Service Bus + Azure Functions for stateless workers
- Azure Durable Functions or AWS Step Functions for multi-step orchestration with timers/retries
- Temporal for hours-to-days workflows with first-class durable execution

Trade-off is the usual one — less ops, more vendor coupling.

### Fan-out belongs on the message bus, not in a synchronous handler loop

A handler that iterates a recipient list inline (`foreach (var follower in followers) await _sender.SendAsync(...)`) holds the request open for N × per-recipient-latency, concentrates the work on one process, and creates traffic spikes that can starve the rest of the system (millions of follower notifications fired by one celebrity post).

**The right shape:** publish **one Wolverine message per recipient** (or per batch of K recipients) and return immediately; per-recipient handlers run in parallel under Wolverine's `MaxDegreeOfParallelism` throttle, set per-handler in `Program.cs`:

```csharp
opts.LocalQueueFor<SendNotificationRequest>().MaxDegreeOfParallelism(N);
```

The throttle gives natural back-pressure — fast producers can't starve slow consumers, and a notification spike doesn't pin a thread or saturate the downstream provider. This is the same principle as "Long-running work belongs on the message bus" applied to fan-out specifically: **accept the work, don't do the work.**

Not retroactively violated today (NotificationService receives one inbound event = one outbound notification), but the rule is preventative for any future broadcast-to-N feature (multi-tenant announcements, post-with-followers, abandoned-cart drips, etc.).

### Entity IDs use `Guid.CreateVersion7()`, not `Guid.NewGuid()`

UUID v7 (first 48 bits = Unix-ms timestamp, remaining 74 bits random) is **time-ordered**, so PK inserts append-extend the B-tree index instead of splitting pages everywhere — kills the index-fragmentation tax that random UUID v4 inserts pay on every write. .NET 9+ API, no third-party package needed, drop-in same `Guid` type.

Apply in aggregate factory methods (`Order.Create`, `Payment.Create`, etc.) — the canonical spot to mint domain IDs.

**Trade-off:** v7's timestamp is decodable from the ID, so the mint time leaks to anyone holding it. Fine for:
- Buyer-scoped resources (IDOR check gates visibility — non-owners can't see the ID at all)
- Naturally-public timestamped resources (Product creation time isn't sensitive; often returned in the response anyway)

**Don't use v7** for IDs where the mint time IS sensitive (security tokens, admin-only internal references).

Existing v4 IDs in the DB stay as-is — v4 and v7 coexist in the same `Guid` column with no migration required; the rule applies to *new* IDs.

### `AsSpan` over `Substring` for zero-allocation slicing — narrow tool

`Substring(...)` allocates a new `string` per call; `AsSpan(...)` returns a `ReadOnlySpan<char>` view over the original with no allocation, and `string.Concat`, `int.Parse`, etc. have span overloads. Real win in *synchronous, hot, string-heavy loops* — parsers, formatters, tokenizers, bulk ID/field manipulation.

**Two guardrails make it a narrow tool, not a default:**
1. It's a micro-optimization governed by "Measure before optimizing" (rule 14) — apply where profiling shows string-allocation pressure, not reflexively
2. **`Span<T>` / `ReadOnlySpan<T>` is a `ref struct` — stack-only, can't cross an `await` boundary or be captured in a lambda/field**, so it's rarely usable inside NextAurora's async-everywhere request handlers (the compiler will stop you)

NextAurora has no such hot path today — its string work is incidental (log templates, IDs), and the bottleneck is always I/O (EF, gRPC, HTTP), never `Substring`. The rule is preventative: *if* a synchronous string-crunching hot path appears and profiling justifies it, reach for `AsSpan`; until then, `Substring` is fine and clearer.

---

## Decision: optimistic concurrency tokens

### Problem

Without a concurrency token, two concurrent requests reading the same aggregate can both write back, and the second write silently overwrites the first — the classic lost-update problem.

Concrete: imagine two admins open the same product in the SellerPortal at the same time. Admin A changes the price; admin B changes the description. Both submit. Without a token, admin B's write loads the old price (which doesn't include A's change), saves their description, and commits — A's price update is gone, no error, no warning. The DB just has B's version.

This matters more in the saga workflow than in admin UI. The Order aggregate transitions through `Placed → Paid → Shipped → Delivered`, driven by event handlers in different services. If the `PaymentCompletedEvent` and `ShipmentDispatchedEvent` arrive close in time and both handlers read the order, both mutate it, both save — one transition is silently dropped.

### What we chose

**Postgres services (Catalog, Shipping):** map a shadow `uint` property to the system `xmin` column.
```csharp
entity.Property<uint>("xmin")
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```
- `xmin` is a system column on every Postgres row. The engine increments it on every write.
- No schema change required. Works against existing tables immediately.
- Configured in [CatalogDbContext.cs](../CatalogService/Infrastructure/Data/CatalogDbContext.cs) (Product, Category) and [ShippingDbContext.cs](../ShippingService/Infrastructure/Data/ShippingDbContext.cs) (Shipment).

**SQL Server services (Order, Payment):** shadow `byte[] RowVersion` column with `IsRowVersion()`.
```csharp
entity.Property<byte[]>("RowVersion").IsRowVersion();
```
- Adds a real `rowversion` column. SQL Server auto-increments on insert/update.
- **Requires a migration** to add the column. See [open issue: migration tooling](#open-issue-migration-tooling-not-wired-up).
- Configured in [OrderDbContext.cs](../OrderService/Infrastructure/Data/OrderDbContext.cs) (Order) and [PaymentDbContext.cs](../PaymentService/Infrastructure/Data/PaymentDbContext.cs) (Payment, Refund).

### Why not the same approach in both?

Postgres has a free-lunch system column (`xmin`) that's perfect for optimistic concurrency — every row already has one, the engine maintains it, no schema changes. SQL Server's `rowversion` (a.k.a. `timestamp`) is conceptually identical but it's a real column you have to add.

We could have used a manual `int Version` property on the entity itself in both. That would unify the two providers, but it (a) leaks infrastructure concerns into the domain, (b) requires the entity to remember to call `Version++` on mutation, and (c) the manual approach is strictly worse than the engine-managed approaches in both providers. Shadow properties keep the domain entities clean and let each provider use its native mechanism.

### Why not `UseXminAsConcurrencyToken()`?

That convenience method existed in Npgsql 8 and earlier. It was removed in Npgsql 9+. The manual shadow-property form shown above is now canonical. Most blog posts still show the old API; ignore them. The skill has been updated to reflect this.

### Tradeoffs we accepted

- **No concurrency exception handling yet.** With tokens in place, two concurrent updates will surface as `DbUpdateConcurrencyException` from `SaveChanges`. Currently no command handler catches this — it'll bubble up as a 500. Need to either retry, surface 409, or apply a merge strategy. See [open issue: concurrency exception handling](#open-issue-concurrency-exception-handling).
- **Token check fails on every write, even when there's no contention.** This is the entire point — every write does an extra `WHERE xmin = @originalXmin` clause. The cost is one column comparison per UPDATE, negligible.
- **Idempotent event handlers handle some of this.** Per [docs/architecture.md "Idempotent Event Handling"](architecture.md), event handlers already check entity status before mutating (e.g., `MarkAsPaid()` only works if status is `Placed`). That catches *some* concurrent-update cases (the second handler sees the wrong status and no-ops). But status-based idempotency only protects against the specific transitions you've encoded — it doesn't protect against, e.g., two `UpdateProduct` commands racing to change different fields. Concurrency tokens are the general solution.

---

## Decision: read/write method split (CQRS data access)

This is documented in detail in [docs/cqrs-data-access.md](cqrs-data-access.md). Summary here for completeness.

### What we chose

Read paths and write paths use **different code shapes** in the handler — there is no repository wrapper (`DbContext` IS Unit-of-Work, `DbSet<T>` IS Repository, see CLAUDE.md "Data access: DbContext directly"):

- **Read handlers** project to DTO inline: `context.Foos.AsNoTracking().Where(...).Select(new FooDto { ... }).ToListAsync(ct)`. Examples: `GetOrderByIdHandler`, `GetOrdersByBuyerHandler`, `GetProductByIdHandler` (cached), `GetAllProductsHandler`, `SearchProductsHandler`, `GetShipmentByOrderHandler`.
- **Write handlers** load the aggregate tracked, mutate via aggregate methods, and call `SaveChangesAsync`. Examples: `PlaceOrderHandler`, `PaymentCompletedHandler`, `UpdateProductHandler`, `ReserveStockHandler`, `CreateShipmentHandler`.

The handler's code shape becomes the proof of intent: `AsNoTracking().Select(DTO)` = read; tracked load + mutate + save = write. No interface ambiguity to resolve.

### Why the split

The pre-refactor design (one shared `GetByIdAsync` per repo) saved a method declaration at the cost of:

- Every read paying for full entity materialization
- Parent-cartesian rows over the wire when a collection `Include` was in play
- An in-memory mapper pass on every read
- A method shape that didn't tell you whether you were on a read or write path

The split costs a few extra lines per handler. Once in place, the entity layer never leaks into the read path — query handlers receive a DTO straight from the IQueryable.

### Where the cache fits

In CatalogService, `GetProductByIdHandler` wraps the inline projection in `IProductCache.GetOrLoadAsync` — the only port that survives in this service's Domain folder, kept because the test fake is real (`Substitute.For<IProductCache>`) and HybridCache vs. test fake is a genuine consumer-substitution case.

---

## Decision: distributed read caching with HybridCache

### Problem

CatalogService's `GetProductByIdQuery` is the highest-frequency read in the system. The storefront fetches product details on every PDP view; `PlaceOrderHandler` does gRPC fan-out per line item to validate stock; recommendation widgets, search-result enrichment, and admin tools all hit the same key. Without caching, each request becomes a Postgres round-trip — fine at 10 RPS, ruinous at 1000 RPS, especially under spiky load (flash sales, scraping bots, schema-warming after a deploy).

The classical answer is cache-aside backed by Redis. Three failure modes lurk in the naive implementation:

1. **Cache stampede.** N concurrent requests for a key that just expired all miss simultaneously, all invoke the load function, all hit the database. The DB sees a synchronized burst that's worse than no caching at all because every replica's MemoryCache misses in lockstep. This isn't theoretical — it's the standard failure mode for a high-traffic single-tier cache after an entry is evicted.
2. **L1/L2 invalidation skew.** A two-tier (in-process + Redis) implementation has to keep both layers consistent on writes. Miss either side and readers on the same replica see stale data until TTL. Doing both layers correctly under concurrency requires per-key coordination that's surprisingly hard to get right.
3. **Serialization protocol drift.** The hand-rolled version usually accretes ad-hoc `JsonSerializer.Serialize(...)` calls. Each call site picks its own options, and over time you have entries written with one shape that can't be deserialized by another — a poison-pill scenario.

### What we chose

`Microsoft.Extensions.Caching.Hybrid` 10.5.0 — .NET 10's official two-tier cache primitive. Implementation in [HybridProductCache.cs](../CatalogService/Infrastructure/Caching/HybridProductCache.cs); abstraction in [IProductCache.cs](../CatalogService/Domain/IProductCache.cs).

```csharp
public sealed class HybridProductCache(HybridCache cache) : IProductCache
{
    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private static string KeyFor(Guid productId) => $"catalog:product:{productId:N}";
    private static string TagFor(Guid productId) => $"product:{productId:N}";

    public Task<ProductDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, Task<ProductDto?>> factory,
        CancellationToken ct = default) =>
        cache.GetOrCreateAsync(
            KeyFor(productId),
            factory: async cancel => await factory(cancel).ConfigureAwait(false),
            options: Options,
            tags: [TagFor(productId)],
            cancellationToken: ct).AsTask();

    public Task InvalidateAsync(Guid productId, CancellationToken ct = default) =>
        cache.RemoveByTagAsync(TagFor(productId), ct).AsTask();
}
```

The handler is a thin cache-aside wrapper over an inline EF projection — see [GetProductById.cs](../CatalogService/Features/GetProductById.cs):

```csharp
public Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken ct)
    => cache.GetOrLoadAsync(
        request.ProductId,
        innerCt => context.Products.AsNoTracking()
            .Where(p => p.Id == request.ProductId)
            .Select(p => new ProductDto { /* projected DTO — no entity hop */ })
            .FirstOrDefaultAsync(innerCt),
        ct);
```

DI registration in [Program.cs](../CatalogService/Program.cs) and [DependencyInjection.cs](../CatalogService/Infrastructure/DependencyInjection.cs):

```csharp
// Program.cs
builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("cache"));
builder.Services.AddHybridCache();

// DependencyInjection.cs
services.AddScoped<IProductCache, HybridProductCache>();
```

### What HybridCache gives us

- **L1 (in-process MemoryCache).** Microseconds, no network. Hot products served without leaving the replica.
- **L2 (distributed Redis).** Milliseconds. Survives process restart; shared across replicas; primary L1 fallback.
- **Stampede protection.** N concurrent misses for the same key invoke the factory once. Implemented internally as keyed async coordination — the in-flight `Task<T>` is what's stored, not just the result, so subsequent callers `await` the first one's work rather than racing past. This is the bit that's hard to do correctly by hand: locking has to be per-key, async-safe, and non-reentrant across awaits inside the factory.
- **Tag-based invalidation.** Each entry carries a per-product tag (`product:{id}`); `RemoveByTagAsync` clears L2 and the *calling replica's* L1 in one operation. Without tags we'd need separate `Remove` calls per layer with a window between them where one layer is fresh and the other is stale. **Important multi-replica caveat — see the "L1 doesn't cross replicas" trade-off below.**
- **Built-in serializer pipeline.** Defaults to source-generated `System.Text.Json` (AOT-friendly, allocation-light, schema-stable). Pluggable via `IHybridCacheSerializer<T>` if a hot type warrants raw protobuf or MessagePack.

### Why `GetOrLoadAsync(factory)` and not `Get / Set / Invalidate`

The earlier sketch of `IProductCache` was three discrete methods. That shape leaks the cache-aside dance into the handler:

```csharp
// rejected
var dto = await cache.GetAsync(id, ct);
if (dto is not null) return dto;
var product = await repository.GetByIdAsync(id, ct);
dto = product is null ? null : Project(product);
if (dto is not null) await cache.SetAsync(id, dto, ct);
return dto;
```

This is broken in two specific ways:

1. **The `Get` then `Set` sequence cannot dedupe concurrent misses.** By the time the second caller calls `Get` and sees a miss, the first caller is already between `Get` and `Set`. Stampede protection requires the cache to know about the in-flight load — it has to hand back the same `Task<T>` to all concurrent miss-callers and `await` it once. That's only possible if the cache *owns* the factory call.
2. **The handler is the wrong owner of the policy.** Every new cached entity in a new handler reinvents the same five lines, and small differences (forgetting to filter null on `Set`, not propagating `CancellationToken`, swallowing exceptions from the load) are how staleness bugs ship.

The factory-based shape pushes all of that into the cache. The handler describes *intent* ("how to load on miss"); the cache owns the *flow* (try L1, try L2, dedupe, run factory, populate both layers, return). Test surface drops to the projection logic — see [ProductCachingTests.cs](../tests/CatalogService.Tests.Integration/ProductCachingTests.cs) (integration tier, exercises the real HybridCache against Testcontainers Postgres + Redis — the right tier for cache-projection behavior).

### What we cache, and why

`ProductDto`, not the EF `Product` entity. Two reasons:

1. **No tracker poisoning.** A cached EF entity carries a navigation graph that's tied to the `DbContext` it was loaded from (which has long since been disposed). Putting it back into a tracker is at best fragile, at worst silently incorrect — change tracking starts believing values that haven't been read from the DB.
2. **The cached unit matches the endpoint output.** `GetProductByIdHandler` returns `ProductDto`; the cache hands back exactly what the handler hands back. No secondary projection on hit.

List queries (`GetAllProducts`, `SearchProducts`) are intentionally not cached. Two reasons: (a) the cache key would have to encode pagination / search parameters (`catalog:products:search:term=foo&page=2&size=50`), and the long tail of unique queries dilutes hit rate to near zero; (b) cross-page invalidation is hard — a single product update would need to invalidate every page that *might* contain it, which means enumerating tags by predicate, which `RemoveByTagAsync` doesn't do.

### Trade-offs we accepted

- **Negative caching.** The factory returning `null` (product not found) is stored as a null entry. Subsequent lookups for that ID skip the DB until TTL. We accept this because product IDs are server-generated GUIDs — there's no "not found now, exists later" race window. If catalog ever supports user-supplied identifiers (slugs, SKUs), this trade flips: we'd filter `null` in the factory or use `HybridCacheEntryFlags.DisableLocalCacheWrite` / `DisableDistributedCacheWrite` selectively.
- **Bounded staleness regardless of writes.** Both L1 and L2 use a 5-minute absolute TTL. After 5 minutes, the next read pays the L2 round-trip + (possibly) the DB. This is fine — TTL is the *safety net* for a missed invalidation in the write path, not the primary consistency mechanism. CLAUDE.md "Cache invalidation in the write path" is the primary mechanism.
- **No probabilistic early refresh.** Large systems sometimes refresh entries at, say, 80% of TTL to avoid synchronized expiry storms. HybridCache supports this pattern via custom flags but we haven't enabled it; under our load profile expiry-clustering hasn't shown up in profiles. On the watchlist for when traffic justifies it.
- **Cache key namespace bound to service.** `catalog:product:{guid}`. The service prefix is deliberate — Redis is shared across services in the AWS deployment (single ElastiCache cluster). The tag (`product:{guid}`) is internal to HybridCache and doesn't need the prefix.
- **Tier-equal TTL.** Both L1 and L2 expire at 5 minutes. We could give L1 a shorter TTL but it complicates the consistency model. We picked simplicity — *but* see the next item before deploying multi-replica.
- **L1 doesn't invalidate across replicas.** `Microsoft.Extensions.Caching.Hybrid` 10.x has **no backplane**. When replica A calls `InvalidateAsync(productId)`, only A's L1 + L2 are cleared; replicas B/C continue serving the stale `ProductDto` from their own L1 for up to `LocalCacheExpiration` (5 min). This is fine today — we run single-replica via Aspire — but it's a correctness bug-in-waiting the moment we deploy 2+ replicas. The API proposal for a pluggable backplane ([dotnet/extensions#5517](https://github.com/dotnet/extensions/issues/5517)) was closed as *"NOT ready for implementation."* Cheapest fix when we deploy multi-replica: drop `LocalCacheExpiration` to 60s (caps the cross-replica staleness window). Real fix: migrate to `FusionCache`, which ships a Redis pub/sub backplane that publishes invalidations across replicas — see [STATUS.md](STATUS.md).
- **L1 memory budget unbounded.** `MemoryCache`'s default size limit is in effect. For catalogs with millions of products we'd cap entries with `SizeLimit` and a `Size` per entry; today's ~1k product seed doesn't justify it.

### Modern .NET / C# 13 features the implementation leans on

| Feature | Where it shows up | Why it matters |
|---|---|---|
| `HybridCache` (10.0+) | The whole class | Ships the L1+L2 + stampede + tags pattern as a primitive. Did not exist in .NET 9. |
| Primary constructor | `HybridProductCache(HybridCache cache) : IProductCache` | One-liner injection; no boilerplate field; less ceremony than the constructor-and-readonly-field form. |
| Collection expression | `tags: [TagFor(productId)]` | Replaces `new[] { ... }` for single-element arrays; intent is clearer at the call site. |
| `ValueTask` ↔ `Task` adaptation | `cache.GetOrCreateAsync(...).AsTask()` | HybridCache returns `ValueTask<T>` to skip allocation on synchronous L1 hits; our public contract returns `Task<T>` to keep the seam framework-agnostic. We pay the allocation only at the boundary, not on every hit. |
| Source-generated `System.Text.Json` | Default `IHybridCacheSerializer` | AOT-compatible; reflection-free; allocation-friendly. Stable across deploys (no expression-tree rebuild on first hit). |
| Nullable reference types | `Func<CancellationToken, Task<ProductDto?>>` | Encodes "negative cache is intentional" in the type — a non-nullable factory would have no way to signal absence. |
| `ConfigureAwait(false)` | Inside the factory adapter | Library code path; we don't capture sync context. |
| `IHybridCacheSerializer<T>` (extension point) | Default suffices for now | Hot types could opt into `MessagePack` or `protobuf-net` without changing call sites. Pluggable, not foreclosed. |

### What we deliberately did not do

- **Read-through cache as the only data interface.** Tempting (handlers depend only on the cache), but it would force every read path through the cache even for queries that don't benefit (search, filters, paginated list). The seam is read-side single-entity only, by design.
- **Write-through cache on updates.** Updates go to the DB and *invalidate* rather than re-populate the cache. Reasoning: the next read recomputes the projection, and we don't have to map mutation → projection in two places. The cost is one cold read after a write, which is fine and self-healing.
- **Eager warming.** No background job pre-populates the cache on app start. Worth considering if profiling shows post-deploy latency spikes, but unwarranted today.
- **Per-tenant key partitioning.** NextAurora is single-tenant. If multi-tenancy lands, the key becomes `catalog:tenant:{tid}:product:{guid}` and the tag becomes `tenant:{tid}:product:{guid}` so `RemoveByTagAsync` stays per-tenant.
- **Distributed lock for invalidation.** Single-replica today means *read-your-writes within the same process* is straightforward — the scoped DI container shares the L1. Strict cross-replica linearizability is out of scope and won't be delivered by HybridCache alone (see the "L1 doesn't invalidate across replicas" trade-off); when multi-replica matters, FusionCache's backplane covers cross-replica notification, not consensus.

### Operational story

- **Observability.** HybridCache emits OpenTelemetry metrics for hit/miss/factory invocations under the `Microsoft.Extensions.Caching.Hybrid` meter. Already picked up by the OTLP exporter wired in `NextAurora.ServiceDefaults` — no extra configuration. Once dashboards exist, hit-ratio and factory-latency belong on the catalog SLO board.
- **Failure isolation.** L2 (Redis) down? `HybridCache` falls back to L1-only and continues serving. L1 hits unaffected; L1 misses go to factory. Worth verifying with a chaos test once integration testing exists; until then, treat as designed-in-but-unverified.
- **Replica deployment model.** Single-replica today, both Aspire-locally and (when shipped) in any future deploy. Multi-replica deployment is *blocked on* either dropping `LocalCacheExpiration` to bound the cross-replica staleness window or migrating to FusionCache. Don't scale out the catalog service horizontally without doing one of those first.
- **Cancellation.** The factory takes a `CancellationToken` that propagates from the originating HTTP request through the handler into the repository call. Client disconnects abandon the load instead of doing wasted work — see CLAUDE.md "Async on request paths."
- **Connection management.** Redis connection is multiplexed via the shared `IConnectionMultiplexer` registered by `AddStackExchangeRedisCache`. No per-request connections; connection holds are bounded by HybridCache's own usage.

### Future work

- **Multi-replica L1 invalidation.** Conditional — only when we deploy multi-replica. Two options, in order: (a) shorten `LocalCacheExpiration` to 60s as a cheap mitigation; (b) migrate to [FusionCache](https://github.com/ZiggyCreatures/FusionCache) which ships a Redis pub/sub backplane that publishes invalidations across replicas. Filed in [STATUS.md](STATUS.md) alongside the polyrepo migration sketch — same shape, both are conditional on the project's nature changing.
- **Cross-service cache invalidation via domain events.** When a `ProductPriceChanged` event is published from CatalogService, downstream services that hold product projections in their own caches (search, recommendations) should subscribe and invalidate. Not yet — those services don't exist. The pattern would be a Wolverine handler in each subscribing service that calls `IProductCache.InvalidateAsync` on receipt.
- **Probabilistic early refresh** if expiry-clustering shows up in dashboards.
- **Per-tenant partitioning** when multi-tenancy lands.
- **Per-type budget tuning.** When the catalog grows past ~100k products, set `SizeLimit` on the underlying `MemoryCache` and a `Size` per entry so L1 doesn't unboundedly consume process memory.

---

## Decision: when to reach past EF Core (Dapper escape hatch)

### Problem

EF Core handles ~95% of our read patterns well — `AsNoTracking()` + projection produces tight SQL, the LINQ stays readable, and we get strong typing across schema changes. The remaining ~5%:

- **Provider-specific SQL** — Postgres `ILIKE`, trigram operators, full-text search; SQL Server `MERGE`, hint syntax; window functions used in reporting — doesn't translate cleanly through LINQ.
- **Hot-path projections** where every microsecond matters and EF's expression-tree compilation, identity-map check, and DbContext bookkeeping show up in profiles.
- **Aggregate / reporting queries** where the SQL is the obvious expression of the intent and the LINQ equivalent obscures it.

Without an explicit answer for these cases, two failure modes appear: people contort LINQ into something that performs worse than raw SQL would have, or they reach for `SqlConnection` / `NpgsqlConnection` directly and lose the parameterization, transaction sharing, and pooling we already get from EF.

### What we chose

`Dapper` 2.1.72 referenced from each Infrastructure project that has a relational DB (Catalog, Order, Payment, Shipping). **Not registered in DI; not a new abstraction.** The package is on the path; the pattern below is the only sanctioned way to use it.

### The pattern: share the EF connection (and transaction)

EF's `DbContext` already owns a connection — and, while a write is in flight, a transaction. Reach into it rather than opening a separate connection:

```csharp
public sealed class ProductReportRepository(CatalogDbContext ctx)
{
    public async Task<IReadOnlyList<TopSellerRow>> GetTopSellersAsync(
        DateOnly since, int limit, CancellationToken ct)
    {
        // Same connection EF already opened for this scope.
        var connection = ctx.Database.GetDbConnection();

        // Postgres-specific SQL that's awkward in LINQ.
        const string sql = """
            SELECT p.id AS Id, p.name AS Name, COUNT(*) AS Sold
            FROM products p
            JOIN order_lines ol ON ol.product_id = p.id
            JOIN orders o ON o.id = ol.order_id
            WHERE o.placed_at >= @Since
            GROUP BY p.id, p.name
            ORDER BY Sold DESC
            LIMIT @Limit;
            """;

        var rows = await connection.QueryAsync<TopSellerRow>(
            new CommandDefinition(sql, new { Since = since, Limit = limit }, cancellationToken: ct));
        return rows.AsList();
    }
}
```

Why this exact shape:

1. **Transaction sharing.** If the calling handler has an EF transaction open (e.g., during a Wolverine `AutoApplyTransactions` chain), `GetDbConnection()` returns the connection that transaction is bound to, and Dapper queries automatically participate in it. No `BeginTransaction` in the Dapper code.
2. **Connection lifetime.** EF disposes the connection when the scope ends. Dapper neither owns nor closes it. No `using` block, no double-dispose risk.
3. **Single pool slot per request.** Opening a separate `NpgsqlConnection`/`SqlConnection` would consume a second pool slot for the same request, doubling pool pressure for no reason. CLAUDE.md "DB connection hold time" applies just as much to Dapper as to EF.
4. **No new abstraction layer.** No `IDbConnectionFactory`, no `IDapperContext`, no decorator. The `DbContext` already in DI is the seam.

### When to reach for Dapper — and when not to

**Do** reach for it when one of these holds:

- The query uses provider-specific SQL that doesn't translate cleanly (`ILIKE`, full-text search, window functions in Postgres; `MERGE`, hint syntax in SQL Server).
- Profiling (`dotnet-counters`, `BenchmarkDotNet`, `EXPLAIN ANALYZE`) shows the EF version is the bottleneck on a hot path. *Profiling first is non-negotiable* — see CLAUDE.md "Measure before optimizing."
- The query is fundamentally a SQL aggregation and writing the LINQ equivalent obscures intent (reporting, dashboards).

**Don't** reach for it when:

- The query is a straightforward CRUD read. EF projection is fine and gives compile-time safety on schema changes.
- You're trying to avoid learning EF's projection syntax. The CQRS query handlers in this codebase show the pattern; learn that first.
- It's a write. Writes go through aggregates and the change tracker — Dapper bypasses both, which means no concurrency token check, no domain validation, no outbox staging. Wholly the wrong tool.
- You haven't profiled. Speculative Dapper rewrites of working LINQ are a classic optimization-without-measurement antipattern.

### Trade-offs we accepted

- **No compile-time column safety.** Dapper maps by name from the result set to property names. A column rename in a migration silently breaks the Dapper query at runtime; EF would catch it at compile time. **Mitigation:** every Dapper query needs an integration test that hits a real (Testcontainer) database and asserts the round-trip.
- **No automatic snake_case ↔ PascalCase mapping.** EF Core has a naming convention pipeline; Dapper doesn't. Either alias columns in SQL (`SELECT p.product_id AS Id`) or flip `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true` once at startup. The example above uses explicit aliases — preferred because it makes the contract local to the query.
- **No change tracking.** Already noted as a feature, not a bug — Dapper is for read paths.
- **Manual cancellation propagation.** Dapper takes `CancellationToken` via `CommandDefinition` (not as a final parameter on `QueryAsync`). Wrap each call in a `CommandDefinition` so the token is honored — see CLAUDE.md "Async on request paths."

### Why not register `IDbConnectionFactory`

A common alternative is to register an `IDbConnectionFactory` that hands out fresh `NpgsqlConnection`/`SqlConnection` instances. We deliberately didn't, for three reasons:

1. **Transaction sharing breaks.** A separately-opened connection isn't enrolled in the ambient EF transaction; you'd have to thread `IDbContextTransaction.GetDbTransaction()` through manually or accept the dual-write hazard.
2. **Pool pressure doubles.** Each request that mixes EF and Dapper holds two pool slots instead of one.
3. **It implies Dapper is the peer of EF.** It isn't — it's the escape hatch. Making it look like a peer abstraction encourages spec-bypass usage.

The convention "go through the DbContext's connection" is a feature, not a limitation.

### Where it applies today

**Nowhere yet.** Dapper is plumbing that's available; no current query has a measured reason to use it. When the first reason arrives, the example above is the canonical shape to copy. Until then, treat the package's presence as "the tool is on the bench" — not as a license to use it.

---

## Concurrency hazards: what the build enforces

Concurrency bugs surface under load, not in dev — by the time symptoms appear, the offending PR is buried in `git log`. The defense is to make the compiler refuse the bad shapes outright. Most of the classical C# concurrency mistakes are already build-failures here; the remaining gap is closed by `Microsoft.CodeAnalysis.BannedApiAnalyzers` reading [`BannedSymbols.txt`](../BannedSymbols.txt) at the repo root.

### The mapping

| # | Hazard | Enforced by | Notes |
|---|---|---|---|
| 1 | Sync-over-async (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`) | **Sonar S4462** + Meziantou MA0042 (build error via `TreatWarningsAsErrors`) | Fires inside async methods. Synchronous entry points use `await app.RunAsync()` so it never appears legitimately. |
| 2 | `lock(this)`, `lock` on string, `lock` on type | **Sonar S2445 / S2444** + CA2002 | General `lock(privateObject)` is allowed — it's sometimes correct. The dangerous shapes are banned. |
| 3 | `async void` outside event handlers | **Sonar S3168** + Meziantou MA0040 | Fires unconditionally on void async methods. UI event handlers (none today) would need a per-method suppression with justification. |
| 4 | `Task.WaitAll`, `Task.WaitAny`, `Parallel.For`, `Parallel.ForEach`, `Thread.Sleep` | **`Microsoft.CodeAnalysis.BannedApiAnalyzers` rule RS0030** via [`BannedSymbols.txt`](../BannedSymbols.txt) | Each banned API has a custom error message pointing at the right replacement (e.g. `await Task.WhenAll`, `Parallel.ForEachAsync`, `await Task.Delay`). |
| 5 | Shared static mutable collections (`static List<T>`, `static Dictionary<K,V>` written across threads) | **Pre-merge grep** (no analyzer covers it cleanly — see below) | Static collections that are *immutable after type-init* are fine and common. Distinguishing requires reading the surrounding code. |
| 6 | Missing `CancellationToken` propagation | **Meziantou MA0032 / MA0040** | Forces forwarding the token to overloads that accept one. Framework-standard hits where the token comes from `HttpContext.RequestAborted`, `ServerCallContext.CancellationToken`, or `EndpointFilterInvocationContext` are recognized. |
| 7 | UI-thread violations (`Dispatcher.Invoke`, `Control.Invoke`) | **N/A — no UI yet** | Storefront is Blazor WASM (no UI thread issues outside WebWorkers); SellerPortal is a static-file scaffold. Add WPF/WinForms-aware analyzers when reactive UI lands. |

### What the build catches today

A deliberately-bad probe was added to verify wiring before this section was written:

```csharp
public static class Probe
{
    public static void Test()
    {
        System.Threading.Thread.Sleep(0);                                       // RS0030
        System.Threading.Tasks.Task.WaitAll(new[] { Task.CompletedTask });      // RS0030
    }
}
```

Build output:

```
error RS0030: The symbol 'Thread.Sleep(int)' is banned in this project:
  Use 'await Task.Delay(ms, ct)' instead — Thread.Sleep blocks the thread.
error RS0030: The symbol 'Task.WaitAll(params Task[])' is banned in this project:
  Use 'await Task.WhenAll(...)' instead — Task.WaitAll blocks the thread.
```

The probe was deleted; the wiring is the wiring.

### The one pattern that's still manual: hazardous shared static state

Rule #5 doesn't have a clean analyzer. The line between "harmless lookup table built at type-init" and "shared mutable state across threads" is structural, not syntactic — `static readonly Dictionary<int, string> Lookup = new() { ... }` is fine as a constant; `static Dictionary<int, string> _cache = new();` written from request handlers is a bug. A grep can flag the *shape*, but a human has to read the surrounding code.

When reviewing a PR that adds a static collection, ask: *is anything ever written to it after type-init?* If yes and the writes can race, the answer is `ConcurrentDictionary<K,V>`, `FrozenDictionary<K,V>`, `ImmutableList<T>`, or `Channel<T>` — never the plain `System.Collections.Generic` types.

```bash
# Quick grep when you want to spot all the static collections in one go:
grep -rnE "static\s+(readonly\s+)?(List|Dictionary|HashSet|Queue|Stack)<" \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj --exclude-dir=Tests.Unit
```

### What this section does not catch

The build catches the *shapes*. It can't catch the *behaviors* — those need load:

- **Cache stampede / thundering herd.** HybridCache mitigates this for `IProductCache`; everywhere else needs measurement under simulated cold-start.
- **Connection-pool exhaustion.** Only visible at sustained RPS with realistic query mix.
- **GC pauses from allocation-heavy hot paths.** `dotnet-counters` against a running stack is the tool, not source review.

These are what the [STATUS.md "Perf baselines under sustained load"](STATUS.md) follow-up exists to surface. The compiler enforcement and the load measurement are complements.

---

## Resolved: transactional outbox via Wolverine

This was the highest-priority correctness gap. **Resolved.** Wolverine's transactional outbox is now configured in OrderService, PaymentService, and ShippingService.

### What we chose

Wolverine 5.36+ ships a transactional outbox/inbox built on top of the messaging persistence packages. We added:

- **Order, Payment** (SQL Server): `WolverineFx.SqlServer` package. `opts.PersistMessagesWithSqlServer(connectionString, "wolverine")`.
- **Shipping** (Postgres): `WolverineFx.Postgresql` package. `opts.PersistMessagesWithPostgresql(connectionString, "wolverine")`.

All three services now run with this combination in `Program.cs`:

```csharp
opts.PersistMessagesWithSqlServer(connectionString, "wolverine");   // or PersistMessagesWithPostgresql
opts.UseEntityFrameworkCoreTransactions();
opts.Policies.AutoApplyTransactions();
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
```

Plus `builder.Services.AddResourceSetupOnStartup()` to auto-create the outbox tables at startup.

The outbox tables live in a `wolverine` schema in each service's existing database — same DB, separate schema, so the outbox row write is in the same transaction as the entity write.

### Why we chose this over the alternatives

The other paths were considered and rejected:

- **Explicit `BeginTransaction` in handlers** doesn't actually solve the dual-write problem — `bus.PublishAsync` writes to Service Bus, which the transaction can't roll back. Pseudo-fix.
- **Hand-rolled outbox** (using the existing `EventLogs` table) would have worked but required building a dispatcher `BackgroundService` per service, a polling loop, and dedup logic. Wolverine has all of this built in.
- **Inbox + idempotent retries** (accept at-most-once publish, rely on idempotent consumers) doesn't address the missed-publish failure modes — idempotency protects against duplicate consumption, not lost messages.

### What this fixes

The three failure modes from before are now handled:

- **Bus publish fails** — outbox row stays unflushed; Wolverine's background dispatcher retries with exponential backoff. The entity is committed (along with the outbox row) or the whole thing rolls back.
- **Process crash between save and publish** — same fix. The outbox row is durable on disk; on restart, Wolverine's dispatcher picks it up and sends.
- **Bus publish succeeds, save commit fails** — can't happen anymore. With `AutoApplyTransactions()`, the publish is staged into the outbox table inside the same transaction. Either both commit or both roll back.

### What still needs attention

**Runtime verification.** Build passes and all 133 unit tests pass — but the outbox semantics aren't covered by unit tests. We don't have integration tests yet (architecture doc lists them as "Not Yet Implemented"). Verifying that Wolverine actually wraps each handler in a transaction and stages messages requires either:

- An integration test that simulates a bus failure mid-handler and asserts no event leaks
- Manual verification by running the app, triggering a publish, and inspecting the `wolverine.*` schema during the request

Until that verification lands, treat this as "configured correctly per docs, behavior unverified at runtime."

**Handler signature design.** The current handlers depend on `IEventPublisher` which wraps `IMessageBus.PublishAsync()`. This works because Wolverine's `AutoApplyTransactions()` walks the dependency graph and detects EF DbContexts transitively through repositories. If runtime testing shows that detection isn't reliable for our setup, the fallback is to refactor handlers to one of:

1. Take `IMessageBus` directly (drop the `IEventPublisher` wrapper).
2. Take `IDbContextOutbox<TDbContext>` for explicit outbox semantics.
3. Use cascading return values (`Task<(TResponse, TEvent)>`) — Wolverine auto-publishes the second value after `SaveChanges`.

We deferred this refactor because it's a bigger change across all command handlers and unit tests, and `AutoApplyTransactions` may handle our pattern correctly without it. If verification fails, option 3 (cascading) is the most idiomatic and the smallest behavioral change per handler.

**SQL Server schema for outbox tables.** The outbox tables are in a `wolverine` schema. SQL Server creates schemas on demand if you have permissions; in dev (Aspire-spun containers, full sysadmin) this is fine. In production with restricted DB users, the `wolverine` schema and the user's CREATE permission within it must be provisioned ahead of time.

### Migration impact

The `RowVersion` columns on Order/Payment (added during the concurrency-token work) ship in each service's `InitialCreate` migration; the `wolverine.*` schema is auto-created at startup by `AddResourceSetupOnStartup()`. In dev, `MigrateDatabaseAsync<T>()` runs on app startup and brings everything up cleanly. For production deployment, see [resolved: migration tooling](#resolved-migration-tooling-wired-up) — the tooling exists but the deploy pipeline does not yet run migrations as a separate pre-deploy step.

---

## Resolved: migration tooling wired up

### What we set up

- **`Microsoft.EntityFrameworkCore.Design`** package referenced from each `*.Infrastructure` project (and from `CatalogService.Api` directly, since Catalog doesn't transitively pull EF Design via Wolverine like the event-publishing services do).
- **`IDesignTimeDbContextFactory<T>`** for each context: [OrderDbContextFactory.cs](../OrderService/Infrastructure/Data/OrderDbContextFactory.cs), [PaymentDbContextFactory.cs](../PaymentService/Infrastructure/Data/PaymentDbContextFactory.cs), [CatalogDbContextFactory.cs](../CatalogService/Infrastructure/Data/CatalogDbContextFactory.cs), [ShippingDbContextFactory.cs](../ShippingService/Infrastructure/Data/ShippingDbContextFactory.cs). Each reads a connection string from `ConnectionStrings__<dbname>` env var with a localhost fallback for design-time use only.
- **`InitialCreate` migrations** generated for all four services. Concurrency tokens are baked in: `RowVersion` columns for the SQL Server services, `xmin` shadow properties for the Postgres services.
- **`MigrateDatabaseAsync<T>()`** extension on `IServiceProvider` in [NextAurora.ServiceDefaults](../NextAurora.ServiceDefaults/Extensions.cs) — opens a scope, resolves the context, calls `Database.MigrateAsync`. Wired into each service's `Program.cs` inside the `app.Environment.IsDevelopment()` block.
- **`.editorconfig`** updated to suppress style/analysis rules in `**/Migrations/**.cs` (generated code shouldn't fail the build on file-scoped namespace, etc.).
- **`app.Run()` → `await app.RunAsync()`** across all four services because the migrate-at-startup `await` made the implicit `Main` async (this also clears the long-standing `S6966` IDE warning).

### How a migration round-trip looks now

```bash
# Add a migration after editing entity config:
dotnet ef migrations add AddSomething \
  --project OrderService \
  --startup-project OrderService

# Apply it (dev): just restart the service. MigrateDatabaseAsync runs at startup.
# Apply it (prod, future): run as a deploy step before app traffic resumes.
```

### Production deployment caveat

`MigrateDatabaseAsync` runs in-process at app startup — fine for dev (single Aspire-managed instance, fresh containers) but unsafe for production with multiple replicas (race on the migration history table; first replica wins, the others may still be running an older version against the new schema).

For production, migrations should run as a **separate deploy step** before app pods receive traffic. The architecture doc lists this under "Not Yet Implemented" — the tooling now exists, the deploy automation doesn't.

---

## Resolved: orphaned EventLogs / replay endpoints deleted

The hand-rolled `EventLogs` table, the `EventLogEntry` entity, and the `/admin/events/...` endpoints were deleted. Wolverine's transactional outbox now lives in a dedicated `wolverine` schema; replay/audit can be done through Wolverine's `IMessageStore` API or by querying that schema directly.

What was removed:
- `EventLog.cs` from `OrderService/Infrastructure/EventLog/`, `PaymentService/Infrastructure/EventLog/`, `ShippingService/Infrastructure/EventLog/` (and the directories).
- `EventLogs` `DbSet<>` and `OnModelCreating` config from each DbContext.
- `AdminEventEndpoints.cs` (admin GET/replay/replay-chain) from each Api project.
- `app.MapAdminEventEndpoints()` registrations in each `Program.cs`.
- `ServiceBusClient` singleton DI registrations from each Infrastructure DI module (only used by the replay endpoints).
- The 418-line `docs/event-replay.md` reduced to a short stub pointing at the new approach.
- Stale references updated in `architecture.md`, `observability.md`, `event-driven-observability.md`, `event-catalog.md`, and `CLAUDE.md`.

`AdminKeyEndpointFilter` (a generic filter for protecting admin routes) was preserved for future use.

---

## Resolved: concurrency exception handling

With concurrency tokens in place, every command handler that does read-modify-save will throw `DbUpdateConcurrencyException` if a concurrent write got there first. We handle that on two layers:

### HTTP path → 409 Conflict via `GlobalExceptionHandler`

The shared [GlobalExceptionHandler](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs) (in `NextAurora.ServiceDefaults`) maps `DbUpdateConcurrencyException` to a 409 ProblemDetails:

```csharp
DbUpdateConcurrencyException => new ProblemDetails
{
    Status = StatusCodes.Status409Conflict,
    Title = "Concurrent modification",
    Detail = "The resource was modified by another request. Refetch and try again.",
    Extensions = { [TraceIdKey] = traceId }
}
```

The caller refetches and decides what to do. This is the right response for HTTP commands (admin-initiated updates, etc.) where the user can react.

### Service Bus path → Wolverine retry

For event handlers (driven by Azure Service Bus), retry is correct: the event is still valid, the handler just needs to read the latest state and reapply. We added a Wolverine error policy in `NextAurora.ServiceDefaults`:

```csharp
public static WolverineOptions AddConcurrencyRetry(this WolverineOptions opts)
{
    opts.OnException<DbUpdateConcurrencyException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
    return opts;
}
```

Called from each event-publishing service's `Program.cs`: `opts.AddConcurrencyRetry()`. Three retries with increasing cooldown; after exhaustion the message goes to the dead-letter queue, where it shows up as a `messages.abandoned` metric (per `architecture.md`).

The status guards in domain methods (`MarkAsPaid()` checks status is `Placed`) handle the "operation no longer valid" case naturally — the retry hits the guard, throws `InvalidOperationException`, and the message is acked rather than DLQ'd (Wolverine treats domain exceptions outside the retry filter as terminal).

### Concrete saga example

`PaymentCompletedEvent` and `ShipmentDispatchedEvent` both arrive while the order is in `Placed`. Both handlers fetch the order, both try to mutate.

- One commits first. Order is now in `Paid`, `xmin` advanced.
- The other's `SaveChanges` throws `DbUpdateConcurrencyException`.
- Wolverine catches it, waits 50ms, retries.
- Retry refetches the order — now in `Paid`. The handler tries `MarkAsShipped()` which checks `Status == Paid` — passes. New write succeeds.

If the retry races again, the cooldown grows (100ms, 250ms). After three failures, DLQ.

### What's still missing

**Wolverine's retry-on-exception model only fires for handlers Wolverine controls.** HTTP requests that go through the Wolverine `IMessageBus.InvokeAsync(...)` pipeline (which is how our endpoints route to handlers) DO have retry middleware applied — `AddConcurrencyRetry()` covers them. But the same exception bubbles up to `GlobalExceptionHandler` if all retries fail, where the 409 path takes over. So an HTTP caller may see a 409 if the system is under unusual contention; that's the correct response.

---

## Remaining cleanup

Tracked here for visibility — none are correctness or performance blockers.

- **Production migration deploy step.** Tooling exists; deploy automation doesn't. `MigrateDatabaseAsync<T>()` runs in-process at startup, gated on `IsDevelopment()`. Production should run migrations as a separate pre-deploy step to avoid replica races. See [resolved: migration tooling](#resolved-migration-tooling-wired-up).
- ~~**Read/write repository separation.**~~ **Done 2026-05-24.** Every read path now projects to a DTO in EF via a dedicated read method (VSA: sibling DTO methods on the existing repo interface; Clean: `IProductReadStore` in Application). See [docs/cqrs-data-access.md](cqrs-data-access.md) for the full pattern.
- **Integration tests.** The outbox semantics, concurrency-retry behavior, and saga choreography aren't covered by unit tests (correctly — those are integration concerns). Architecture doc lists "Integration Tests" under "Not Yet Implemented." Once that pipeline exists, write tests that exercise the outbox under simulated bus failures and concurrency conflicts.
- **EF tools version skew.** `dotnet ef` CLI is at 9.0.8 while the runtime targets EF 10.0.2 — emits a non-fatal advisory each time. Update the global tool when convenient: `dotnet tool update --global dotnet-ef`.

---

## Specifications cross-reference

When you need to discuss specific decisions, here's where the source-of-truth lives:

| Topic | Spec location |
|---|---|
| Hard rules every PR must follow | [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules) |
| EF Core deep guidance, modern features, concurrency, plumbing, migrations | [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md) |
| CQRS handler inventory, read/write method split, projection-in-EF rule | [docs/cqrs-data-access.md](cqrs-data-access.md) |
| System architecture, polyglot persistence, communication patterns | [docs/architecture.md](architecture.md) |
| Event topology, contracts, lifecycle | [docs/architecture.md "Event-Driven Architecture"](architecture.md#event-driven-architecture), [docs/event-catalog.md](event-catalog.md) |
| Correlation/User/Session propagation | [docs/context-propagation.md](context-propagation.md), [CLAUDE.md "Observability & Context Propagation"](../CLAUDE.md#observability--context-propagation) |
| Event replay (admin endpoints) | [docs/event-replay.md](event-replay.md) |
| Aggregate invariants & business rules | [docs/architecture.md "Domain Model"](architecture.md#domain-model) |
| Concurrency token configuration per service | [CatalogDbContext.cs](../CatalogService/Infrastructure/Data/CatalogDbContext.cs), [OrderDbContext.cs](../OrderService/Infrastructure/Data/OrderDbContext.cs), [PaymentDbContext.cs](../PaymentService/Infrastructure/Data/PaymentDbContext.cs), [ShippingDbContext.cs](../ShippingService/Infrastructure/Data/ShippingDbContext.cs) |
| Read-cache contract & implementation | [IProductCache.cs](../CatalogService/Domain/IProductCache.cs), [HybridProductCache.cs](../CatalogService/Infrastructure/Caching/HybridProductCache.cs) |
| EF Core spec & practice — reference walkthrough | [docs/ef-core.md](ef-core.md) |
| Build settings (warnings as errors, analyzers) | [Directory.Build.props](../Directory.Build.props) |
| Package versions (CPM) | [Directory.Packages.props](../Directory.Packages.props) |

If a spec lives in two places and they disagree, CLAUDE.md wins for hard rules; the docs win for design rationale; the skill wins for "how do I implement this." All known stale references to pre-Wolverine infrastructure (`LoggingEventPublisher`, `ServiceBusEventPublisher`, `LoggingBehavior`, `EventLogs` table, `/admin/events`) have been removed across CLAUDE.md, README.md, architecture.md, and the supporting docs.

---

## What changed when

A short audit trail of how this guide's content came to exist, so you can explain "why is rule X here?" to teammates.

| When | Change | Driver |
|---|---|---|
| Before this work | CLAUDE.md had no perf rules. Guidance scattered across reading, intuition, code review. | Pre-existing. |
| Initial perf rules pass | Added 9 always-on perf rules to CLAUDE.md, created `dotnet-performance` skill from a generic .NET perf guide, tailored to NextAurora. | Reading [Kerim Kara's "0 to 1M Users" .NET perf guide](https://medium.com/@kerimkkara). |
| Critique pass on AsNoTracking | Added `AsNoTrackingWithIdentityResolution()` exception to the rule, expanded skill's tracking-strategy section. | Reading [Kerim Kara's "EF Core optimization that doubles CPU"](https://medium.com/@kerimkkara) — his core point about `AsNoTracking + Include` duplicating shared related entities is real. The "always AsNoTracking" rule alone misses this. |
| Modern EF expansion | Added 4 more rules (bulk ops, concurrency tokens, outbox atomicity, DbContext thread-safety) and 2 more (migration immutability, measure-before-optimizing). Skill grew sections on modern EF features (ExecuteUpdate, AsSplitQuery, compiled queries), plumbing (interceptors, query filters), migration hygiene. | [Milan Jovanović's EF Core best-practices](https://www.milanjovanovic.tech) reference. The original skill missed EF 7+ features and the whole transactional/concurrency story. |
| Codebase audit | Identified concrete violations: missing concurrency tokens (all 6 aggregates), unpaginated list endpoints (3), outbox separation in 3 handlers, missing CancellationToken on ~13 endpoints. Confirmed false positives: AsNoTracking-on-shared-methods is intentional per [cqrs-data-access.md](cqrs-data-access.md). | Audit pass after rules were finalized. |
| Concurrency token implementation | Added xmin shadow property to Catalog and Shipping (Postgres). Added RowVersion shadow property to Order and Payment (SQL Server). Solution builds clean; all 133 unit tests pass. SQL Server changes need migrations (not yet wired). | This conversation. |
| Wolverine outbox investigation | Confirmed CLAUDE.md's outbox claim is stale. `LoggingEventPublisher` doesn't exist; `EventLogs` is orphaned (only contains replay records). Documented as open issue with four fix options and a recommendation. | This conversation. |
| Skill API correction | `UseXminAsConcurrencyToken()` was removed in Npgsql 9+. Updated skill to show the manual shadow-property form. | Build error during this conversation. The old API is what most blog posts still show. |
| Wolverine outbox implementation | Added `WolverineFx.SqlServer` (Order, Payment) and `WolverineFx.Postgresql` (Shipping). Configured `PersistMessagesWith*`, `UseEntityFrameworkCoreTransactions`, `AutoApplyTransactions`, `UseDurableOutboxOnAllSendingEndpoints`. Outbox tables live in a `wolverine` schema in each service's DB; auto-created on startup via `AddResourceSetupOnStartup()`. Build clean, all 133 tests pass. Runtime semantics unverified pending integration tests. | This conversation. |
| Package bumps blocking outbox work | Bumped `WolverineFx*` 5.17 → 5.36.2 (transitive `Microsoft.CodeAnalysis.Workspaces` conflict on 5.17). Bumped `OpenTelemetry.*` 1.14.0 → 1.15.x (4 CVEs newly surfaced by NuGet audit during the restore). Pre-existing tech debt unblocked as a side effect. | This conversation. |
| CLAUDE.md outbox section refreshed | Removed stale `LoggingEventPublisher` reference; added the Wolverine outbox config snippet; clarified that `EventLogs`/replay endpoints are an audit log, not the outbox. | This conversation. |
| Pagination on unbounded list endpoints | `GET /api/v1/products`, `GET /api/v1/products/search`, `GET /api/v1/orders/buyer/{buyerId}` now require `page`/`pageSize` with server-side cap (default 50, max 100). Repository methods `GetAllAsync`, `SearchAsync`, `GetByBuyerIdAsync` updated to take pagination + apply `OrderBy + Skip + Take`. CancellationToken added to all production-path endpoints. | This conversation. |
| Concurrency exception handling | `GlobalExceptionHandler` maps `DbUpdateConcurrencyException` to 409 Conflict with refetch advice. New `AddConcurrencyRetry()` extension on `WolverineOptions` adds a 3-attempt backoff retry policy (50/100/250ms cooldowns) — wired into Order, Payment, Shipping. After exhaustion, message goes to DLQ. | This conversation. |
| `EventLogs` deletion | Removed the orphaned `EventLog` entity, DbSets, OnModelCreating configs, `AdminEventEndpoints` files, and `ServiceBusClient` DI registrations from Order, Payment, Shipping. `event-replay.md` reduced to a 20-line stub pointing at Wolverine's outbox/`IMessageStore` instead. CLAUDE.md, architecture.md, observability.md, event-driven-observability.md, event-catalog.md updated to remove stale references. | This conversation. |
| EF migration pipeline | Initial migrations generated for all 4 services (Catalog, Order, Payment, Shipping). `IDesignTimeDbContextFactory<T>` per service uses env var with localhost fallback. New `MigrateDatabaseAsync<T>()` extension in ServiceDefaults runs at startup in dev only. Migrations include `RowVersion` (SQL Server) and `xmin` (Postgres) concurrency-token columns from the entity configs. `.editorconfig` updated to opt out generated migration files from style rules. `app.Run()` switched to `await app.RunAsync()` everywhere (cleared a recurring `S6966` warning). | This conversation. |
| URL-segment API versioning | Added `Asp.Versioning.Http` + `Asp.Versioning.Mvc.ApiExplorer` 10.0.0. New `AddNextAuroraApiVersioning()` extension wired into `AddServiceDefaults()` so every service inherits the same policy: default 1.0, `UrlSegmentApiVersionReader`, `AssumeDefaultVersionWhenUnspecified=false`. All four endpoint extensions now use `app.NewVersionedApi(...)` with `/api/v{version:apiVersion}/...` routes. `Results.Created`/`Results.Accepted` Location headers updated. README, architecture.md, BRD.md (SCL-04), CLAUDE.md updated. Hard cutover (no compat shim for unversioned URLs) since there are no external consumers yet. | This conversation. |
| Distributed read caching with HybridCache | Replaced the earlier single-tier Redis-via-`IDistributedCache` design with `Microsoft.Extensions.Caching.Hybrid` 10.5.0 (L1 in-process MemoryCache + L2 Redis, stampede protection, tag-based invalidation). `IProductCache` reshaped from `Get / Set / Invalidate` to factory-based `GetOrLoadAsync(factory) / InvalidateAsync` so the framework owns the cache-aside flow. `HybridProductCache` replaces `RedisProductCache`. Handler dropped to a one-liner. Build clean, all 134 tests pass. Full rationale: [decision section](#decision-distributed-read-caching-with-hybridcache). | This conversation. |
| OpenAPI YAML output | Added `app.MapOpenApi("/openapi/{documentName}.yaml")` alongside the existing JSON endpoint in all five services. .NET 9+'s built-in OpenAPI emitter switches format on the route extension. Useful for tooling that prefers YAML (Spectral, embedding in markdown, some Postman/Insomnia imports). | This conversation. |
| Scalar API reference UI | Added `Scalar.AspNetCore` 2.14.11 + `app.MapScalarApiReference()` in all five services. Reads the existing OpenAPI doc and renders an interactive UI at `/scalar/v1` (dev-only). | This conversation. |
| Dapper escape hatch | Added `Dapper` 2.1.72 to the four Infrastructure projects with relational DBs (Catalog, Order, Payment, Shipping). No DI registration — the sanctioned pattern is `ctx.Database.GetDbConnection()` so Dapper queries share the EF connection + transaction. Plumbing only; no current query uses it. Full pattern + when-to-reach-for-it: [decision section](#decision-when-to-reach-past-ef-core-dapper-escape-hatch). | This conversation. |
| Concurrency hazards: build-enforced | Audited the seven classical C# concurrency mistakes against current analyzer coverage. Six of seven are now build-failures: Sonar S4462/S2445/S2444/S3168, Meziantou MA0032/MA0040/MA0042, plus newly-added `Microsoft.CodeAnalysis.BannedApiAnalyzers` 3.3.4 fed by [`BannedSymbols.txt`](../BannedSymbols.txt) banning `Task.WaitAll`/`WaitAny`, `Parallel.For`/`ForEach`, `Thread.Sleep` (with replacement guidance in each error message). The one exception is "shared static mutable collections" — too structural for a pure-syntactic analyzer; documented as a code-review check. Section reframed from "manual grep checklist" to "what the build enforces and what it can't." | This conversation. |
