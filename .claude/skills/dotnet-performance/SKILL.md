---
name: dotnet-performance
description: Performance & EF Core guidance for .NET 10 services — query tuning, AsNoTracking pitfalls, AsSplitQuery, ExecuteUpdate/Delete, compiled queries, optimistic & pessimistic concurrency, multi-SaveChanges transactions, DbContext thread-safety / IDbContextFactory, SaveChangesInterceptor, global query filters, soft delete, bulk inserts, migrations hygiene, caching (in-memory/Redis) & invalidation, async/await pitfalls, API pagination & compression, GC pressure & object pooling, structured logging cost, DB connection pooling, middleware ordering, benchmarking with BenchmarkDotNet/k6 and ToQueryString diagnostics. Use when writing or reviewing handlers, queries, repositories, endpoints, processors, middleware, or migrations where throughput, latency, allocations, scale, correctness under concurrency, or transactional integrity matter.
---

# .NET Performance Guidance

Reference for performance-sensitive work in NextAurora. The rules in CLAUDE.md ("Performance Rules") are the always-on hard rules; this file is the deeper "why" and the broader playbook.

**Mindset:** measure first, optimize what matters. Most perf problems aren't framework-level — they're small decisions (an unindexed query, an unnecessary allocation, a blocking await) that compound under load.

---

## 1. EF Core — query patterns

EF Core is the single biggest source of perf issues in .NET services. Most fall into four buckets.

### Project, don't load full entities

Bad — pulls every column, every row, with change tracking:
```csharp
var users = context.Users.ToList();
```

Better — projection, no tracking, only the columns the caller needs:
```csharp
var users = await context.Users
    .AsNoTracking()
    .Select(u => new UserDto(u.Id, u.Name))
    .ToListAsync(cancellationToken);
```

For our CQRS layout: **queries return DTOs via projection**, never tracked entities. Commands load the aggregate (tracked) because they mutate it.

### Kill N+1 with `Include` or projection

Bad:
```csharp
var orders = await context.Orders.ToListAsync(ct);
foreach (var o in orders)
    o.Items = await context.OrderItems.Where(i => i.OrderId == o.Id).ToListAsync(ct);
```

Eager load:
```csharp
var orders = await context.Orders.Include(o => o.Items).ToListAsync(ct);
```

Project (best for read paths — minimal columns, no tracking):
```csharp
var orders = await context.Orders
    .AsNoTracking()
    .Select(o => new OrderDto(o.Id, o.Items.Select(i => i.Name).ToArray()))
    .ToListAsync(ct);
```

### Tracking strategy: projection > `AsNoTracking()` > `AsNoTrackingWithIdentityResolution()`

Three modes:

| Mode | Tracking | Identity resolution | When to use |
|---|---|---|---|
| Default (tracked) | Yes | Yes | Commands / writes — load aggregate, mutate, save |
| `.AsNoTracking()` | No | **No** | Reads where you project to a DTO (no shared refs to dedupe) |
| `.AsNoTrackingWithIdentityResolution()` | No | Yes | Reads where you must materialize an entity graph with `Include` and rows share related entities |

**The trap:** `.AsNoTracking().Include(o => o.Customer)` on 500 orders that all share 1 customer creates **500 Customer objects**, not 1. Tracking gives you identity resolution for free; turning it off removes it. If you need the entity graph (rare in our CQRS — queries should project), use `AsNoTrackingWithIdentityResolution()`.

If you're projecting to a DTO, this whole concern disappears — flat data has no shared references to dedupe. **Prefer projection.**

### Batch writes; don't `SaveChanges` in a loop

```csharp
context.AddRange(items);
await context.SaveChangesAsync(ct);   // one round-trip, not N
```

For very large inserts, see **Bulk operations** below.

### Indexes

Even a perfect EF query is slow without indexes. Every filter/join/sort column on a hot path needs one. Add indexes via fluent config in `IEntityTypeConfiguration<T>`. Composite indexes often serve multiple queries; prefer one composite index over several single-column indexes.

Watch the other direction too: every index has a write cost (every `INSERT`/`UPDATE` touching the column updates the index). Don't add indexes speculatively.

### Always async, always cancellable

```csharp
await context.Orders.ToListAsync(ct);   // never .ToList()
```

Sync EF calls block a thread. Every async method on a request path takes and propagates `CancellationToken`.

### Diagnostics: `ToQueryString()`

LINQ hides the SQL. Inspect it before trusting it:
```csharp
var sql = context.Orders.Where(o => o.CreatedAt >= since).ToQueryString();
_logger.LogDebug("Generated SQL: {Sql}", sql);
```
Or enable EF query logging in `appsettings.Development.json` (`"Microsoft.EntityFrameworkCore.Database.Command": "Information"`).

---

## 2. EF Core — modern features (EF 7+/8+/9+)

### `ExecuteUpdateAsync` / `ExecuteDeleteAsync` — set-based, no tracker

For bulk updates/deletes, skip the change tracker entirely:
```csharp
await context.Products
    .Where(p => p.Price < 10)
    .ExecuteUpdateAsync(s => s
        .SetProperty(p => p.IsDiscounted, true)
        .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

await context.OutboxMessages
    .Where(m => m.ProcessedAt < DateTime.UtcNow.AddDays(-30))
    .ExecuteDeleteAsync(ct);
```

Translates to a single SQL `UPDATE`/`DELETE`. Massively faster than load-mutate-save for bulk ops (outbox cleanup, status flips, soft-delete sweeps).

**Caveat:** runs outside the change tracker. Tracked entities in the same context become stale. Don't mix `ExecuteUpdate` with `SaveChanges` on the same entities in the same unit of work.

### `AsSplitQuery()` — for cartesian explosions

Default behavior collapses multiple `Include`s into one SQL with joins. For multiple **collection** includes, the result set explodes (Cartesian product). Split into multiple round-trips:
```csharp
var order = await context.Orders
    .Include(o => o.LineItems)
    .Include(o => o.Shipments)
    .AsSplitQuery()
    .FirstOrDefaultAsync(o => o.Id == id, ct);
```

**When to use:** multiple collection includes returning many rows each. **When not to use:** a single collection include, or includes of reference (`*-to-one`) navs — the join is fine and one round-trip beats many. Profile with EF logging if unsure.

Note: split queries shift work from DB to app (EF stitches results in memory). Don't reach for it as a default.

### Compiled queries

EF compiles LINQ → SQL on every execution. For a query that runs millions of times with different parameters, precompile:
```csharp
private static readonly Func<AppDbContext, Guid, CancellationToken, Task<Order?>> GetOrderById =
    EF.CompileAsyncQuery((AppDbContext ctx, Guid id, CancellationToken ct) =>
        ctx.Orders.FirstOrDefault(o => o.Id == id));

var order = await GetOrderById(context, id, ct);
```

EF already caches query plans, so the win is small for most apps. **Reach for this only after a profiler shows query translation is meaningful**, and only on simple, unchanging queries. Not worth the rigidity for dynamic filters.

### Bulk inserts

Three tiers, fastest to slowest:

1. **Native bulk APIs** — Postgres `COPY` via `Npgsql.NpgsqlBinaryImporter`, or `SqlBulkCopy` for SQL Server. Hundreds of thousands of rows/sec. Use for ETL, seed data, large imports.
2. **`EFCore.BulkExtensions`** (third-party) — `BulkInsertAsync`, `BulkUpdateAsync`. Convenient middle ground.
3. **`AddRange` + batched `SaveChanges`** — fine for <1k rows. Above that, EF's per-row overhead and tracking dominate.

In our services, the outbox publish path is fine with `AddRange` + `SaveChanges`. Reach for bulk APIs only for genuinely high-volume work (seed jobs, imports, archive moves).

---

## 3. EF Core — concurrency & transactions

### Optimistic concurrency

For aggregates that can be updated by concurrent requests (orders, payments, carts), guard with a row-version / concurrency token.

**Postgres (Npgsql 10+):** map a shadow property to the system `xmin` column. The convenience method `UseXminAsConcurrencyToken()` was removed in Npgsql 9; the manual form is now canonical:
```csharp
modelBuilder.Entity<Order>(e =>
    e.Property<uint>("xmin")
        .HasColumnName("xmin")
        .HasColumnType("xid")
        .ValueGeneratedOnAddOrUpdate()
        .IsConcurrencyToken());
```
No schema change — `xmin` is a system column on every Postgres row, auto-incremented by the engine on write. Older blog posts still show `UseXminAsConcurrencyToken()`; that API is gone.

**SQL Server:** shadow `byte[] RowVersion` column with `IsRowVersion()`:
```csharp
modelBuilder.Entity<Order>().Property<byte[]>("RowVersion").IsRowVersion();
```
This adds a real `rowversion` column → requires a migration.

When two transactions read the same row and both try to update, the second `SaveChanges` throws `DbUpdateConcurrencyException`. Handle it: refetch, reapply the operation, or surface a 409 to the client. Without a concurrency token, last-write-wins silently corrupts state.

### Multi-`SaveChanges` transactions

`SaveChanges` is implicitly transactional for one call. Anything spanning multiple `SaveChanges` (or multiple DbContexts) needs an explicit transaction:
```csharp
await using var tx = await context.Database.BeginTransactionAsync(ct);
try
{
    // step 1: update aggregate
    await context.SaveChangesAsync(ct);
    // step 2: write outbox row
    await context.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
}
catch
{
    await tx.RollbackAsync(ct);
    throw;
}
```

**Outbox-relevant:** the entity write and the outbox-row write must commit atomically. Either both happen or neither does. CLAUDE.md mandates this; the mechanism is either (a) save both in one `SaveChanges` call (preferred — EF handles the transaction) or (b) explicit transaction across two `SaveChanges` calls.

### `DbContext` is **not thread-safe**

Two parallel queries on one context = race conditions and `InvalidOperationException`. For parallel queries, inject `IDbContextFactory<AppDbContext>` and create one context per task:
```csharp
// BAD — shared DbContext across parallel tasks
var t1 = context.Orders.ToListAsync(ct);
var t2 = context.Customers.ToListAsync(ct);
await Task.WhenAll(t1, t2);   // crashes intermittently

// GOOD — context per task
await using var ctx1 = await _factory.CreateDbContextAsync(ct);
await using var ctx2 = await _factory.CreateDbContextAsync(ct);
var t1 = ctx1.Orders.ToListAsync(ct);
var t2 = ctx2.Customers.ToListAsync(ct);
await Task.WhenAll(t1, t2);
```

Register both alongside your scoped context:
```csharp
builder.Services.AddDbContextFactory<AppDbContext>(opt => opt.UseNpgsql(cs));
```

In practice, the scoped per-request `DbContext` is the right default and parallelism within a request is rare. But if you do parallelize (e.g., a dashboard fanning out to several reads), use the factory.

### Pessimistic locking — only when optimistic isn't enough

For read-modify-write where you must block other readers (account balances, inventory decrements), acquire a row lock:
```csharp
var account = await context.Accounts
    .FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {id} FOR UPDATE")
    .FirstAsync(ct);
```

Postgres `FOR UPDATE` (or `FOR NO KEY UPDATE`) holds until the transaction commits/rolls back. Other transactions block. Use sparingly — it serializes contention and tanks throughput. Optimistic concurrency is the default; pessimistic is the exception.

---

## 4. EF Core — plumbing

### `SaveChangesInterceptor` for cross-cutting concerns

Auto-set timestamps, dispatch domain events, write audit rows. Single source of truth, runs for every `SaveChanges`:
```csharp
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added) entry.Entity.CreatedAt = now;
            if (entry.State == EntityState.Modified) entry.Entity.ModifiedAt = now;
        }
        return base.SavingChangesAsync(eventData, result, ct);
    }
}

// Register:
builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
    opt.UseNpgsql(cs).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
```

This is the natural home for **outbox publishing**: enumerate `ChangeTracker.Entries<IHasDomainEvents>()`, append outbox rows for each domain event, clear the events. All in the same transaction as the entity save.

### Global query filters — soft delete, multi-tenancy

```csharp
modelBuilder.Entity<Customer>().HasQueryFilter(c => !c.IsDeleted);
modelBuilder.Entity<Order>().HasQueryFilter(o => o.TenantId == _tenantContext.CurrentTenantId);
```

Every query on the entity gets the `WHERE` clause silently. Opt out with `.IgnoreQueryFilters()` for admin/diagnostic queries.

**Watch out:** filters cascade through `Include`s, which can over-filter unexpectedly (e.g., excluding a soft-deleted parent hides its non-deleted children too). Test the edges.

### Raw SQL escape hatches

When LINQ can't express it (recursive CTEs, window functions, vendor-specific):
```csharp
// Read into a typed projection:
var rows = await context.Database
    .SqlQuery<RevenueRow>($"SELECT date, sum(amount) AS revenue FROM ledger WHERE date >= {since} GROUP BY date")
    .ToListAsync(ct);

// Modify (no return):
await context.Database.ExecuteSqlAsync($"CALL refresh_materialized_view({name})", ct);
```

Always `FromSqlInterpolated` / `SqlQuery<T>($"...")` (parameterized via interpolation) — never `FromSqlRaw` with string concat (SQL injection).

---

## 5. Migration hygiene

Migrations are code that runs against production data. Treat accordingly.

- **One migration per logical change.** Don't bundle a column rename with a new index and a data backfill — when one piece needs reverting, you can't.
- **Never edit an applied migration.** Once it's run anywhere (dev included), it's immutable. Need a fix? New migration.
- **Destructive changes need a plan.** Dropping a column is irreversible. Stage it: deploy code that no longer reads the column → wait one release → drop the column in a follow-up migration. Same for table renames (add new, dual-write, backfill, switch reads, drop old).
- **Backfills go in their own migration** or as a separate data step — not mixed with schema changes. Long-running backfills can lock tables.
- **Rebase migrations carefully.** EF generates a model snapshot; merging snapshots is painful. Always pull, then add your migration.
- **Test against prod-shape data.** Dev-DB migrations on 100 rows tell you nothing about a 50M-row table. Run against a restored prod backup or a sized staging DB before deploying.
- **Postgres specifics:** `ALTER TABLE ADD COLUMN` with a non-volatile default is fast in PG 11+ (metadata-only). Adding `NOT NULL` to an existing column rewrites the table — backfill first as nullable, then alter.

For long-running migrations, prefer manual SQL with `CONCURRENTLY` (e.g., `CREATE INDEX CONCURRENTLY`) over EF-generated migrations that take exclusive locks.

---

## 6. Caching

Three layers; pick one based on consistency needs and replica count.

| Strategy | When | Trade-off |
|----------|------|-----------|
| `IMemoryCache` | Single-instance, low-stakes data | Fast, but per-instance — cache stampede & inconsistency across replicas |
| Redis (`IDistributedCache`) | Multi-instance, shared truth | Network hop, serialization cost, but coherent |
| Hybrid (in-memory L1 + Redis L2) | Hot read paths | Most complex; only when measured |

### In-memory pattern

```csharp
public Task<User> GetUserAsync(int id, CancellationToken ct) =>
    _cache.GetOrCreateAsync($"user:{id}", entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        return _users.FindAsync(id, ct).AsTask();
    })!;
```

### Redis pattern

```csharp
var key = $"user:{id}";
var cached = await _redis.GetStringAsync(key, ct);
if (cached is not null) return JsonSerializer.Deserialize<User>(cached)!;

var user = await _users.FindAsync(id, ct);
await _redis.SetStringAsync(key, JsonSerializer.Serialize(user),
    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
return user;
```

### Invalidation is the hard problem

Every write path that mutates a cached entity must remove or replace the cache entry **in the same logical operation** as the DB write. Forgetting this serves stale data — usually for hours, sometimes forever.

For domain events, prefer **invalidate on the event handler** (e.g., `ProductPriceChanged` handler removes `product:{id}` from cache) over invalidating at the call site.

### Cache strategies

- **Cache-aside** (lazy load on miss) — default, simple, good for most cases.
- **Write-through** — write cache and DB together. Good when reads vastly outnumber writes.
- **Write-behind** — write cache, async-flush DB. Risky; data loss on crash.

---

## 7. Async & concurrency

### Never block async

```csharp
// BAD — sync-over-async, threadpool starvation, deadlocks under SynchronizationContext
var s = httpClient.GetStringAsync(url).Result;
var s = httpClient.GetStringAsync(url).GetAwaiter().GetResult();

// GOOD
var s = await httpClient.GetStringAsync(url, ct);
```

### Don't make trivially-sync methods async

```csharp
// BAD — Task allocation, state machine, no real async work
public async Task<int> GetNumber() => 42;

// GOOD
public int GetNumber() => 42;
// or if interface requires Task:
public Task<int> GetNumberAsync() => Task.FromResult(42);
```

### `ValueTask` for hot paths that *usually* complete synchronously

E.g., an `IMemoryCache` lookup that hits 99% of the time. Don't use it everywhere — `Task` is fine and easier to reason about.

### Parallelism — bounded

Unbounded `Task.WhenAll` can exhaust a downstream:
```csharp
await Parallel.ForEachAsync(urls,
    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
    async (url, c) => await httpClient.GetStringAsync(url, c));
```

For parallel DB queries, see §3 — use `IDbContextFactory<T>`, not the scoped context.

### `CancellationToken` everywhere

Every async method on a request path must accept and propagate `CancellationToken`. Without it, cancellation is best-effort and timeouts leak.

### `ConfigureAwait(false)` in libraries only

ASP.NET Core has no `SynchronizationContext`, so it's a no-op in our service code. Skip it.

---

## 8. API design

### Always paginate list endpoints

```csharp
public async Task<IResult> GetUsers(int page = 0, int size = 20, CancellationToken ct = default)
{
    if (size > 100) size = 100;   // hard cap server-side
    var users = await _db.Users.AsNoTracking()
        .OrderBy(u => u.Id).Skip(page * size).Take(size)
        .Select(u => new UserDto(u.Id, u.Name))
        .ToListAsync(ct);
    return Results.Ok(users);
}
```

For large offsets (`Skip(100000)`), switch to **keyset pagination** (`WHERE id > lastSeenId`) — `OFFSET` is O(N) on the DB.

### Response compression

Already on by default in our ServiceDefaults; verify `app.UseResponseCompression()` runs **before** endpoints, **after** `UseHttpsRedirection`.

### Output caching for hot, public, idempotent reads

```csharp
app.MapGet("/products", ...).CacheOutput(p => p.Expire(TimeSpan.FromMinutes(1)));
```

Don't cache responses that vary by user without a `VaryByValue`/`VaryByHeader` — you'll leak data across users.

### Rate limiting

Per CLAUDE.md, search and payment endpoints have rate limiting. Extend to any endpoint that can be hammered (login, checkout). Use `AddFixedWindowLimiter` or `AddTokenBucketLimiter`.

### Stateless

Sessions belong in Redis or a JWT, not process memory. We're horizontally scalable; in-process state breaks that.

---

## 9. Memory & GC

The GC is automatic, not free. Allocations on a hot path → Gen 0 churn → Gen 1/2 promotions → STW pauses → tail latency spikes.

### Reuse collections in tight loops

```csharp
// BAD — N allocations
for (int i = 0; i < N; i++) { var list = new List<int>(); list.Add(i); }

// GOOD
var list = new List<int>(capacity: 16);
for (int i = 0; i < N; i++) { list.Clear(); list.Add(i); }
```

### `ArrayPool<T>` for transient buffers

Anything `≥ 85,000 bytes` lands on the **Large Object Heap**, rarely compacted → fragmentation. Pool buffers:
```csharp
var pool = ArrayPool<byte>.Shared;
var buf = pool.Rent(100_000);
try { /* use buf */ }
finally { pool.Return(buf); }
```

### Strings: `StringBuilder` or interpolation handlers, not `+=` in loops

```csharp
// BAD — N intermediate strings
var s = ""; for (int i = 0; i < 1000; i++) s += i;

// GOOD
var sb = new StringBuilder(); for (int i = 0; i < 1000; i++) sb.Append(i);
```

### `Span<T>` / `ReadOnlySpan<T>` for parsing

Avoid substringing in hot parsers — slice spans instead.

### `struct` vs `class`

Small (≤ 16 bytes), short-lived, value-semantic → `readonly struct`. Don't make large structs — copies become more expensive than allocations.

### Watch closure allocations

```csharp
items.Where(i => i.Value > threshold);   // closure per call
```
Fine in cold paths; hoist or cache the delegate in hot paths if a profiler shows it.

---

## 10. Logging

Logging in hot paths is a stealth perf killer.

### Use message templates, not interpolation

```csharp
// BAD — string allocated even when log level is disabled
_logger.LogInformation($"User {user.Name} logged in at {DateTime.UtcNow}");

// GOOD — structured, cheap if filtered out
_logger.LogInformation("User {UserName} logged in at {Time}", user.Name, DateTime.UtcNow);
```

This also gives queryable structured fields (essential for our correlation/user/session scope from CLAUDE.md).

### Don't log inside tight loops

Summaries, not item-by-item:
```csharp
// BAD
foreach (var item in items) _logger.LogInformation("Processing {Id}", item.Id);

// GOOD
_logger.LogInformation("Processing {Count} items", items.Count);
```

### Use the right level

`Trace`/`Debug` are filtered out in prod by default — safe to leave in. `Information` runs everywhere; budget it. Errors and warnings should be rare and **actionable**.

### Source-generated logging for very hot paths

```csharp
[LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "User {UserId} logged in")]
static partial void LogUserLoggedIn(ILogger logger, Guid userId);
```
Compile-time, zero-allocation. Use in handlers/processors that run thousands/sec.

---

## 11. Database connections

ADO.NET pools connections by `(connection string, credentials)`. Two failure modes:

### Hold time

Anything between `OpenAsync()` and `Dispose` holds a slot. **Don't `await` non-DB work while a connection is open.**

```csharp
// BAD
await using var conn = new NpgsqlConnection(_cs);
await conn.OpenAsync(ct);
var data = await conn.QueryAsync<...>(sql);
await SomeHttpCall(ct);   // pool slot held during HTTP

// GOOD — close the connection before unrelated awaits
List<X> data;
await using (var conn = new NpgsqlConnection(_cs))
{
    await conn.OpenAsync(ct);
    data = (await conn.QueryAsync<X>(sql)).ToList();
}
await SomeHttpCall(ct);
```

### Pool exhaustion

Symptoms: latency cliff, `OpenAsync` timeouts, requests queue up. Causes: connection leak (missing dispose), pool too small, queries too slow. Fix root cause; don't just bump `Max Pool Size`.

### Dapper for hot, simple read paths

EF Core is the default. For high-RPS read paths where ORM overhead matters and the query is one statement, Dapper is materially faster:
```csharp
var user = await conn.QueryFirstOrDefaultAsync<UserDto>(
    "SELECT id, name FROM users WHERE id = @id", new { id }, cancellationToken: ct);
```
Don't use this for write paths or anything that needs the unit of work.

---

## 12. Middleware

Runs on **every request**. Order matters; logic in the wrong place runs N× more than needed.

### Keep middleware thin

Auth, correlation, logging, rate limiting, compression — yes. Business logic — no. Put it in handlers.

### Order

Roughly: `Exception → HttpsRedirection → ResponseCompression → Routing → CORS → RateLimiter → Authentication → Authorization → Endpoints`. Auth before authorization is mandatory.

### Short-circuit cheap, frequent paths

```csharp
app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
```
Don't run health checks through the full auth stack.

### Don't open per-request resources in middleware

If you need to read the body, buffer once with `EnableBuffering()` — but only on routes that need it. Same for any heavy per-request init.

---

## 13. Benchmarking & load testing

> "It feels fast" is not a measurement.

### Microbenchmarks: BenchmarkDotNet

```csharp
[MemoryDiagnoser]
public class HandlerBenchmarks
{
    [Benchmark] public Task Handle() => _handler.HandleAsync(_cmd, default);
}
// BenchmarkRunner.Run<HandlerBenchmarks>();
```
Gives ns/op, allocations/op, GC stats. Use for hot-path comparisons (projection vs Include, JSON vs MessagePack).

### Load testing: k6 or Azure Load Testing

```js
import http from 'k6/http';
export const options = { vus: 200, duration: '2m' };
export default function () { http.get('https://api.example.com/products?page=0&size=20'); }
```

Watch:
- **P50, P95, P99 latency** — averages lie; tail latency is what users feel.
- **Throughput (RPS)** at the latency SLO, not max RPS.
- **Error rate** — 5xx and timeouts.
- **Resource use** — CPU, working set, GC time, DB pool waits.

### Profiling under realistic load

`dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`, PerfView. **Profile before optimizing** — guesses are usually wrong.

For EF specifically: enable query logging in dev (`Microsoft.EntityFrameworkCore.Database.Command: Information`), or use `ToQueryString()` on a suspect query (§1).

---

## TL;DR — the highest-leverage things

1. **`AsNoTracking()` + projection** on every read query. If you must `Include` an entity graph without tracking, use `AsNoTrackingWithIdentityResolution()` — plain `AsNoTracking()` with `Include` duplicates shared related objects.
2. **`await` everywhere on the request path; never `.Result` / `.Wait()`**. Always pass `CancellationToken`.
3. **Pagination + size cap** on every list endpoint. Keyset pagination for large offsets.
4. **Bulk ops use `ExecuteUpdateAsync` / `ExecuteDeleteAsync`** — don't load-mutate-save thousands of rows.
5. **Optimistic concurrency token** on every aggregate that can be updated by concurrent requests.
6. **Outbox writes commit in the same transaction** as the entity write — one `SaveChanges` (preferred) or explicit `BeginTransactionAsync`.
7. **`DbContext` is not thread-safe** — `Task.WhenAll` parallel queries need `IDbContextFactory<T>`.
8. **Structured logging templates** with `{Param}` placeholders, not string concatenation.
9. **Cache invalidation is part of the write path** — same handler that owns the change.
10. **Hold DB connections briefly** — open, query, dispose; no unrelated awaits in between.
11. **Migrations are immutable once applied; destructive changes need a multi-step plan.**
12. **Measure before optimizing.** BenchmarkDotNet for code, k6 + dotnet-counters for systems, `ToQueryString()` for EF.
