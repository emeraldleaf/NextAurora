# EF Core in NextAurora — Specification & Practice

This is the reference guide to how NextAurora uses EF Core: what we do, why we do it, the trade-offs we accepted, and the rules a reviewer should expect to see honored in any PR that touches data access.

The hard rules summarized at the end of this doc are codified in [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules); deeper background lives in [docs/performance-and-data-correctness.md](performance-and-data-correctness.md). When this doc and CLAUDE.md disagree, CLAUDE.md wins.

## Table of Contents

- [1. Overview — where EF Core fits](#1-overview--where-ef-core-fits)
- [2. Provider matrix — Postgres + SQL Server](#2-provider-matrix--postgres--sql-server)
- [3. DbContext: registration, lifetime, thread safety](#3-dbcontext-registration-lifetime-thread-safety)
- [4. Entity configuration patterns](#4-entity-configuration-patterns)
- [5. Concurrency tokens — `xmin` vs `RowVersion`](#5-concurrency-tokens--xmin-vs-rowversion)
- [6. Migrations](#6-migrations)
- [7. Read-side: `AsNoTracking` + projection](#7-read-side-asnotracking--projection)
- [8. Write-side: tracked load → mutate → SaveChanges](#8-write-side-tracked-load--mutate--savechanges)
- [9. Read/write method split — the hard rule](#9-readwrite-method-split--the-hard-rule)
- [10. Repository pattern — kept, deliberately](#10-repository-pattern--kept-deliberately)
- [11. N+1, `Include`, projection, `AsSplitQuery`](#11-n1-include-projection-assplitquery)
- [12. `AsNoTrackingWithIdentityResolution` — the `Include` trap](#12-asnotrackingwithidentityresolution--the-include-trap)
- [13. Pagination + ordering](#13-pagination--ordering)
- [14. Bulk operations: `ExecuteUpdateAsync` / `ExecuteDeleteAsync`](#14-bulk-operations-executeupdateasync--executedeleteasync)
- [15. Wolverine transactional outbox integration](#15-wolverine-transactional-outbox-integration)
- [16. Optimistic concurrency exception handling](#16-optimistic-concurrency-exception-handling)
- [17. DbContext is not thread-safe — `IDbContextFactory<T>`](#17-dbcontext-is-not-thread-safe--idbcontextfactoryt)
- [18. Connection lifetime](#18-connection-lifetime)
- [19. Dapper escape hatch](#19-dapper-escape-hatch)
- [20. HybridCache invalidation in the write path](#20-hybridcache-invalidation-in-the-write-path)
- [21. Hard rules summary (CLAUDE.md)](#21-hard-rules-summary-claudemd)
- [22. Crib sheet](#22-crib-sheet)

---

## 1. Overview — where EF Core fits

EF Core is our **default data-access tool** for every relational write and for most relational reads. Handlers take `DbContext` directly — there is no `I*Repository` wrapper interface (CLAUDE.md "Data access: DbContext directly"). `DbContext` IS the Unit of Work; `DbSet<T>` IS the Repository. The previous repository-wrapper pattern was removed in the simplicity refactor (and the CatalogService variant in the VSA-collapse refactor that followed); the only thing the wrapper was buying us was the ability to mock handlers in unit tests, and that was replaced with integration tests against Testcontainers.

**Version pin:** EF Core 10.0.2, declared centrally in [Directory.Packages.props](../Directory.Packages.props). All projects reference packages **without versions** thanks to Central Package Management.

**What EF Core handles:**
- All persistence for the four DB-owning services (Catalog, Order, Payment, Shipping)
- Change tracking + dirty-detection on the write path
- Projection to DTOs on the read path
- Migrations
- Optimistic concurrency tokens
- Transaction management (in concert with Wolverine's outbox)
- All the SQL we need *except* the edge cases in §19

**What EF Core does *not* handle:**
- Provider-specific SQL that doesn't translate cleanly through LINQ → see [§19 Dapper escape hatch](#19-dapper-escape-hatch)
- Bulk INSERT (we don't have a use case yet; would be a separate consideration)
- Async-fanout queries from one scope → see [§17 `IDbContextFactory<T>`](#17-dbcontext-is-not-thread-safe--idbcontextfactoryt)

---

## 2. Provider matrix — Postgres + SQL Server

| Service | Provider | DbContext | Concurrency token |
|---|---|---|---|
| Catalog | PostgreSQL | [CatalogDbContext.cs](../CatalogService/Infrastructure/Data/CatalogDbContext.cs) | `xmin` (system column) |
| Shipping | PostgreSQL | [ShippingDbContext.cs](../ShippingService/Infrastructure/Data/ShippingDbContext.cs) | `xmin` (system column) |
| Order | SQL Server | [OrderDbContext.cs](../OrderService/Infrastructure/Data/OrderDbContext.cs) | `RowVersion` (real column) |
| Payment | SQL Server | [PaymentDbContext.cs](../PaymentService/Infrastructure/Data/PaymentDbContext.cs) | `RowVersion` (real column) |
| Notification | none | none | n/a (stateless) |

### Why two providers (the honest answer)

Both Postgres and SQL Server handle every workload here well. We use both because:

1. **Polyglot persistence is the whole point of microservices' "data autonomy" argument.** Different bounded contexts can pick different stores. Mixing providers makes that visible.
2. **It surfaces real EF Core provider differences side-by-side.** Concurrency tokens are the cleanest example — see §5.
3. **It mirrors real enterprise reality** where you often inherit mixed stacks.

A production decision would also weigh licensing cost (Postgres free, SQL Server paid), team expertise, and existing infrastructure. The "Postgres for read-heavy" / "SQL Server for transaction-heavy" rationale in [architecture.md:175-178](architecture.md#L175) is *slightly* overstated — both engines handle either workload — but Postgres genuinely fits Catalog's JSONB/array/full-text needs marginally better, and SQL Server fits the Microsoft-shop reality marginally better for Order/Payment.

### Provider packages

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.2" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.2" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.2" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.2" />
```

---

## 3. DbContext: registration, lifetime, thread safety

Registered as **Scoped** in each service's Infrastructure DI module. Scoped = one instance per HTTP request / per Wolverine message dispatch. Example from [CatalogService.Infrastructure/DependencyInjection.cs](../CatalogService/Infrastructure/DependencyInjection.cs):

```csharp
public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
{
    // DbContext registered as scoped (default). Each HTTP request / Wolverine message
    // dispatch gets its own instance. DbContext isn't thread-safe so one-per-scope avoids
    // accidental sharing, the change tracker stays small (only entities loaded during this
    // request), and connection pooling means the underlying DB connection is still reused.
    services.AddDbContext<CatalogDbContext>(options =>
        options.UseNpgsql(configuration.GetConnectionString("catalog-db")));

    services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>();

    services.AddScoped<IProductCache, HybridProductCache>();

    // Read handlers explicitly registered so integration tests can resolve them
    // directly (Wolverine's handler discovery does NOT register handlers in DI —
    // see CLAUDE.md "Communication Patterns → Wolverine handler discovery is NOT
    // DI registration").
    services.AddScoped<GetProductByIdHandler>();
    services.AddScoped<GetAllProductsHandler>();
    services.AddScoped<SearchProductsHandler>();

    return services;
}
```

### Why scoped, not transient or singleton

- **Singleton** would share the change tracker across all requests → unbounded memory growth + cross-request entity leakage.
- **Transient** would create a new DbContext for every constructor that asks for one *within the same request* — the repository's DbContext would be different from a handler's DbContext, so `SaveChanges` on one would never see entities loaded by the other.
- **Scoped** keeps one DbContext per logical operation. Every collaborator (repository, handler, ambient transaction) sees the same change tracker and connection.

### What "DbContext isn't thread-safe" actually means

The change tracker, the open connection, the command pipeline — none of these are concurrent-safe. **Two `await`-ed queries on the same DbContext in parallel** (e.g. `Task.WhenAll(ctx.Foo.ToListAsync(), ctx.Bar.ToListAsync())`) is the classic crash: you'll get `InvalidOperationException: A second operation was started on this context instance before a previous operation completed.`

If you legitimately need parallel queries → see [§17 `IDbContextFactory<T>`](#17-dbcontext-is-not-thread-safe--idbcontextfactoryt).

### Health check

`.AddDbContextCheck<CatalogDbContext>()` registers a `/health` check that opens a connection and pings the DB. Surfaces in the Aspire dashboard and any orchestrator probing health endpoints.

---

## 4. Entity configuration patterns

Three patterns recur in every DbContext's `OnModelCreating`. Example: [CatalogDbContext.cs](../CatalogService/Infrastructure/Data/CatalogDbContext.cs).

### 4.1 Explicit precision on money

```csharp
entity.Property(e => e.Price).HasPrecision(18, 2);
entity.Property(e => e.Currency).HasMaxLength(3);
```

**Why:** EF's default decimal mapping is lower precision and silently truncates trailing fractional digits. `HasPrecision(18, 2)` = 18 digits total, 2 after the decimal — fits any realistic price up to 999,999,999,999,999.99.

### 4.2 Backing-field navigation for encapsulated collections

Order's children come up here: [OrderDbContext.cs](../OrderService/Infrastructure/Data/OrderDbContext.cs).

```csharp
entity.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.OrderId);

// Tells EF: when materializing this navigation, write into the private backing field
// Order._lines, not through the public read-only Order.Lines property.
entity.Navigation(e => e.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
```

**Why:** Order exposes `Lines` as `IReadOnlyList<OrderLine>` so application code can't `order.Lines.Add(...)` and bypass aggregate invariants. EF needs to *populate* that collection on load, but if it tries to mutate the read-only property it fails. `PropertyAccessMode.Field` makes EF write into `_lines` (the private `List<OrderLine>`) directly — the canonical pattern for properly-encapsulated DDD aggregates.

### 4.3 Enum as string for forward compatibility

```csharp
entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
```

**Why:** Default enum mapping is `int`. If you ever reorder or rename enum members, old rows still resolve to the right name. Cost: ~20 bytes/row vs 4. Readability and migration safety win at our scale.

### 4.4 Required indexes for non-PK lookup columns

```csharp
entity.HasIndex(e => e.CategoryId);
entity.HasIndex(e => e.SellerId);
entity.HasIndex(e => e.BuyerId);
```

**Why:** Without indexes, `WHERE BuyerId = @id` does a full table scan. Even at 100K rows that's a real perf hit on the buyer-order-history endpoint.

---

## 5. Concurrency tokens — `xmin` vs `RowVersion`

**This is one of the most load-bearing topics in the system.** Know it cold.

### 5.1 The problem optimistic concurrency solves

Two concurrent handlers read the same row, both mutate, both call `SaveChanges`. Without a token, the second write silently overwrites the first — the classic **lost-update problem**. With a token, the second `SaveChanges` throws `DbUpdateConcurrencyException`.

Concrete saga example: a `PaymentCompletedEvent` and a `ShipmentDispatchedEvent` arrive close together. Both handlers load the Order. Both mutate. Without a token, one transition is silently dropped.

### 5.2 Postgres: `xmin` shadow property — no schema change

Every Postgres row has a system column `xmin` — the transaction ID that last wrote the row. The engine increments it on every write. Map it as an EF shadow property:

From [CatalogDbContext.cs:63](../CatalogService/Infrastructure/Data/CatalogDbContext.cs#L63):

```csharp
entity.Property<uint>("xmin")
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

EF then includes `WHERE xmin = @originalXmin` on every UPDATE. If another transaction touched the row first, the WHERE matches zero rows and EF throws `DbUpdateConcurrencyException`.

**No schema change required.** The column already exists; we're just binding to it.

**Heads up — old Npgsql API:** `UseXminAsConcurrencyToken()` existed in Npgsql 8 and earlier. It was removed in Npgsql 9+. The manual shadow-property form above is canonical. Blog posts still show the old API; ignore them.

### 5.3 SQL Server: `RowVersion` shadow column — added by migration

SQL Server's equivalent is the `rowversion` (a.k.a. `timestamp`) type. It's a real column the engine auto-increments on insert/update. Unlike `xmin`, this requires a column add.

From [OrderDbContext.cs:51](../OrderService/Infrastructure/Data/OrderDbContext.cs#L51):

```csharp
entity.Property<byte[]>("RowVersion").IsRowVersion();
```

The `InitialCreate` migration includes the column:

```sql
CREATE TABLE [Orders] (
    [Id] uniqueidentifier NOT NULL,
    -- ... other columns ...
    [RowVersion] rowversion NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY ([Id])
);
```

### 5.4 Why different mechanisms per provider

We could use a manual `int Version` property on every entity and increment it ourselves — that would unify the two providers. We chose not to because:

1. It leaks an infrastructure concern (versioning for concurrency) into the Domain entity.
2. Every mutation method has to remember `Version++`. Forgotten increments = silent bugs.
3. Each provider has a native, engine-maintained option that's strictly better. Use what the database gives you.

### 5.5 Aggregates with concurrency tokens today

| Service | Aggregate | Token |
|---|---|---|
| Catalog | Product, Category | Postgres `xmin` |
| Shipping | Shipment | Postgres `xmin` |
| Order | Order | SQL Server `RowVersion` |
| Payment | Payment, Refund | SQL Server `RowVersion` |

### 5.6 The cost

One column comparison per UPDATE. Negligible. **Last-write-wins is not acceptable** ([CLAUDE.md "Performance Rules" → Optimistic concurrency](../CLAUDE.md#performance-rules)).

### 5.7 Exception handling

`DbUpdateConcurrencyException` bubbles out of `SaveChangesAsync`. See [§16](#16-optimistic-concurrency-exception-handling) for how we route it: HTTP gets 409 via the global handler; Wolverine event handlers retry with backoff.

---

## 6. Migrations

### 6.1 What they are

**EF Core migrations are versioned, code-generated database schema changes.** Each one is a C# class that knows how to apply a specific schema delta (and undo it). EF Core uses them to keep your database structure in sync with your entity classes over time.

When you change a `Product` entity in C# — add a property, change a string length, add an index — the database doesn't update itself. You have three options:

| Approach | Problem |
|---|---|
| **Manual `ALTER TABLE` scripts** | Easy to forget; hard to roll back; teammates write conflicting scripts; no link between code change and schema change |
| **Drop-and-recreate** (`EnsureCreated()`) | Fine in unit tests; catastrophic anywhere with real data |
| **Migrations** ← we use this | EF generates the SQL by diffing the current model against a snapshot of the last-applied model. Each migration is committed to git, runs in order on every environment, and history is tracked in a `__EFMigrationsHistory` table inside the database itself. |

A migration is **idempotent on a given database**: EF checks the history table before running each one, so applying twice does nothing. New environments catch up automatically by replaying all migrations in order.

### 6.2 What lives where

Each Infrastructure project's `Migrations/` folder holds three kinds of files, all committed to git:

```
20260503040949_InitialCreate.cs              ← the migration: Up() applies, Down() reverts
20260503040949_InitialCreate.Designer.cs     ← FROZEN snapshot of the model AT this migration
CatalogDbContextModelSnapshot.cs             ← LIVE snapshot of the current model (regenerated every migrations add)
```

The two snapshot files have different jobs:

- **`*.Designer.cs`** captures the model *as it was when this migration was created*. It's immutable from that point on. EF uses it to know what the model looked like at this version (for, e.g., `--idempotent` script generation).
- **`CatalogDbContextModelSnapshot.cs`** is the model *as of right now*. `dotnet ef migrations add` regenerates it after every new migration. It's the baseline EF diffs the next entity change against.

Lose or hand-edit either snapshot and `dotnet ef migrations add` will produce wrong SQL (it has no accurate baseline to diff from).

Inside the database, EF maintains:

```sql
CREATE TABLE __EFMigrationsHistory (
    MigrationId    nvarchar(150) PRIMARY KEY,   -- e.g. '20260503040949_InitialCreate'
    ProductVersion nvarchar(32)
);
```

Every `Migrate()` call: read this table → find entries in `Migrations/` that aren't there yet → run each one's `Up()` in order → insert a row per applied migration.

### 6.3 The pieces in this repo

Each Infrastructure project has:

1. **`Microsoft.EntityFrameworkCore.Design` package** with `PrivateAssets="all"` — build-time only, never shipped at runtime.
2. **`IDesignTimeDbContextFactory<T>` implementation** so `dotnet ef` can construct a context outside the running app. Example: [CatalogDbContextFactory.cs](../CatalogService/Infrastructure/Data/CatalogDbContextFactory.cs):

   ```csharp
   public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
   {
       public CatalogDbContext CreateDbContext(string[] args)
       {
           var cs = Environment.GetEnvironmentVariable("ConnectionStrings__catalog-db")
               ?? "Host=localhost;Database=catalog-db;Username=postgres;Password=postgres";

           var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(cs).Options;
           return new CatalogDbContext(options);
       }
   }
   ```

   Reads connection string from env (Aspire injects it) with a localhost fallback for CLI use. **Why this factory exists at all:** the `dotnet ef` CLI runs in its own process — no Web host, no DI container. It needs *some* way to construct the DbContext. `IDesignTimeDbContextFactory<T>` is the official hook.

3. **`Migrations/` folder** — see [§6.2](#62-what-lives-where).

4. **`MigrateDatabaseAsync<TContext>` extension** in [NextAurora.ServiceDefaults/Extensions.cs:452](../NextAurora.ServiceDefaults/Extensions.cs#L452) — opens a scope, resolves the context, calls `Database.MigrateAsync(ct)`. Called from each service's `Program.cs` inside `if (app.Environment.IsDevelopment()) { ... }`.

### 6.4 Dev round-trip

```bash
# 1. Edit entity / DbContext config (add a property, change a length, add an index)

# 2. Generate the migration
dotnet ef migrations add AddPromotionCodes \
  --project CatalogService \
  --startup-project CatalogService

# 3. Apply: just restart the service. MigrateDatabaseAsync runs at startup in dev.
dotnet run --project NextAurora.AppHost
```

After the VSA collapse, CatalogService is a single Web SDK project — both `--project` and `--startup-project` point at the same csproj. The same shape applies to Order/Payment/Shipping (their migration commands already had a single-project shape).

Behind the scenes, step 2:
- Uses `CatalogDbContextFactory.CreateDbContext` to construct the context outside the app
- Compares the current model against `CatalogDbContextModelSnapshot.cs`
- Emits `AddPromotionCodes.cs` (with `Up()`/`Down()`), `AddPromotionCodes.Designer.cs` (frozen snapshot at this point), and updates `CatalogDbContextModelSnapshot.cs` to match
- You commit all three to git

### 6.5 `Up()` and `Down()` — and why never `Down()` in production

Every migration is a class with two methods:

```csharp
public partial class AddPromotionCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PromotionCode",
            table: "Products",
            type: "nvarchar(50)",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PromotionCode",
            table: "Products");
    }
}
```

`Up()` is what gets applied during `Database.MigrateAsync` (or `dotnet ef database update`). `Down()` is what would undo it via `dotnet ef database update <PreviousMigrationName>`.

**`Down()` is for dev convenience only.** In production with real data:
- `Down()` for a "drop column" migration deletes whatever data was in that column
- `Down()` for a "rename column" migration only restores the column name, not any data that was written under the new name
- `Down()` is generated mechanically from `Up()` — it doesn't know about your data semantics

The right way to back out a migration in production is to **write a new migration that re-introduces what you removed**. That keeps the history forward-only and explicit.

### 6.6 Production — *not* in-process

`MigrateDatabaseAsync` is gated on `IsDevelopment()`. **Why never in-process at startup in production:** with multiple replicas behind a load balancer booting at the same time after a deploy:

- Replica A starts → calls `Migrate()` → acquires the `__EFMigrationsLock` → applies the migration
- Replicas B, C start simultaneously → also call `Migrate()` → block on the lock, then race when A releases
- Outcome: at best, B and C block startup for the duration of the migration; at worst, one of them sees the history table mid-update and crashes with conflicts. Pods restart-loop, ops gets paged.

**Production needs migrations as a separate pre-deploy step** before app pods receive traffic:

```bash
# In CI, before deploying the new app image:
dotnet ef database update \
  --project CatalogService \
  --startup-project CatalogService \
  --connection "$PROD_CATALOG_DB_CONNECTION"
```

Then deploy the app image. The pods boot, hit `IsDevelopment() == false`, skip the `MigrateDatabaseAsync` call, and start serving against the already-up-to-date schema.

The tooling for this exists; the CI automation does not yet. Tracked in [GitHub Issues](https://github.com/emeraldleaf/NextAurora/issues?q=is%3Aissue+is%3Aopen+label%3Aarea%2Finfra).

### 6.7 The immutable-once-applied rule

From [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules):

> Migrations are immutable once applied: never edit a migration that has run anywhere (dev included). Destructive changes (drop column/table, rename, NOT NULL on existing column) need a multi-step plan, not a single migration.

**Why edits break things:** editing an applied migration drifts `CatalogDbContextModelSnapshot.cs` from the `__EFMigrationsHistory` table. The next `dotnet ef migrations add` diffs against a snapshot that doesn't match reality and produces wrong SQL. Worse, deploying the edited version to a DB that already ran the old version either silently no-ops (if the edit was additive) or corrupts schema (if it tries to re-apply changes already in place).

If a migration was wrong, **write a new migration that fixes it**. The old one stays in git as part of history.

**Destructive change recipe** — for drop column, drop table, rename, or `NOT NULL` on existing column:

1. **Deploy code that no longer reads the column.** Old pods are replaced; the column still exists in the schema for any in-flight requests.
2. **Wait one release cycle** so all requests using the old code have completed.
3. **Generate a new migration that drops the column.** Deploy.

Doing all three steps in one migration is the classic "we shipped at 3pm and the API was down by 3:05" story — during the rolling deploy, old pods crash on the missing column while new pods are still rolling out.

### 6.8 Quick concept checks

- **What does `__EFMigrationsHistory` do?** Tracks which migrations have been applied to *this specific database*, so EF knows what to skip on the next `Migrate()` call.
- **What's the snapshot file for?** Two different snapshot files. `CatalogDbContextModelSnapshot.cs` is the live model state EF compares against when generating the *next* migration. The per-migration `*.Designer.cs` is the frozen model state at the time of that migration, used for things like `dotnet ef migrations script --idempotent`.
- **Why not just `EnsureCreated()`?** It creates the schema from the current model in one shot with no history. Fine for tests; fatal in any environment that holds data because you can never *evolve* the schema without dropping it.
- **What does `IDesignTimeDbContextFactory` do?** Lets `dotnet ef` construct your DbContext *without* running the app. The CLI is a separate process and doesn't have your DI container — it needs an explicit hook.
- **What happens if two migrations are added concurrently on different branches?** Merge conflict in `CatalogDbContextModelSnapshot.cs`. Resolution: keep one migration, regenerate the second on top via `dotnet ef migrations remove` → re-add. (Don't merge the snapshot by hand — EF's diff baseline ends up wrong.)
- **When would I use `Down()`?** Local dev only — to undo a migration you're iterating on. Never in production: it's mechanically generated, doesn't understand your data, and silently destroys content.
- **What if my migration generation produces wrong SQL?** Don't edit the generated file. Either delete-and-regenerate (if no one else has the migration) via `dotnet ef migrations remove`, or override the migration body in C# manually if it has already been shared.

---

## 7. Read-side: `AsNoTracking` + projection

The default rule for queries: **`AsNoTracking()` + `.Select(... new Dto ...)`**.

```csharp
// Application/Handlers/GetAllProductsHandler.cs (returns DTOs, not entities)
var products = await repository.GetAllAsync(request.Page, request.PageSize, cancellationToken);
return products.Select(p => new ProductDto
{
    Id = p.Id, Name = p.Name, Description = p.Description,
    Price = p.Price, Currency = p.Currency,
    Category = p.Category?.Name ?? "",
    SellerId = p.SellerId, StockQuantity = p.StockQuantity, IsAvailable = p.IsAvailable
}).ToList();
```

And inside the handler ([GetAllProductsHandler in Features/GetAllProducts.cs](../CatalogService/Features/GetAllProducts.cs)):

```csharp
public async Task<IReadOnlyList<ProductDto>> HandleAsync(GetAllProductsQuery request, CancellationToken cancellationToken)
    => await context.Products.AsNoTracking()
        .OrderBy(p => p.Id).Skip((safePage - 1) * safePageSize).Take(safePageSize)
        .Select(p => new ProductDto { /* ... projection inline ... */ })
        .ToListAsync(cancellationToken);
```

### 7.1 Why `AsNoTracking`

Tracking has per-row cost: EF builds a change-tracker entry for each entity, stored in the identity map, ready to detect mutations at `SaveChanges`. On a read-only path that's pure overhead — we'll never call `SaveChanges`. Skipping it removes:

- The identity-map insertion cost (hash + lookup)
- The change-detection snapshot allocation
- Memory pressure under high read concurrency

For a query returning 50 products with 8 properties each, that's ~50 × 9 (entity + 8 property snapshots) allocations saved per call. Multiplied across hundreds of requests/sec, it's measurable in GC pressure.

### 7.2 Why projection (`Select` to DTO)

Two wins:

1. **EF generates SQL with only the columns we project.** No `SELECT * FROM Products`; instead `SELECT p.Id, p.Name, ...`. Less I/O, smaller result sets, better cache utilization.
2. **No entity graph materialization.** EF builds the DTO directly from the result rows. No `Product` instance is ever created. No tracked-state metadata. No navigation property setup.

The CLAUDE.md rule says it explicitly:

> EF Core reads: always `AsNoTracking()` + projection (`.Select(...)` to a DTO). Queries return DTOs, never tracked entities.

### 7.3 What this rule looks like in practice

| Anti-pattern | Why it's wrong |
|---|---|
| `ctx.Products.ToListAsync()` (no `AsNoTracking`, no projection) | Tracks every row + materializes full entity graph |
| `ctx.Products.AsNoTracking().ToListAsync()` then map manually in C# | No tracking overhead, but still selects all columns |
| `ctx.Products.AsNoTracking().Select(p => new ProductDto { ... }).ToListAsync()` | ✅ correct — minimal SQL, no tracker |

### 7.4 The canonical shape: project inline in the handler, return DTOs

The read path runs an inline `IQueryable` in the handler — no repository wrapper, no in-memory mapper. `AsNoTracking().Where(...).Select(p => new FooDto { ... })` lives in the handler body itself.

```csharp
// CatalogService/Features/GetProductById.cs
public class GetProductByIdHandler(CatalogDbContext context, IProductCache cache)
{
    public Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken)
        => cache.GetOrLoadAsync(
            request.ProductId,
            ct => context.Products.AsNoTracking()
                .Where(p => p.Id == request.ProductId)
                .Select(p => new ProductDto { /* ... */ })
                .FirstOrDefaultAsync(ct),
            cancellationToken);
}

// OrderService/Features/GetOrderById.cs
public class GetOrderByIdHandler(OrderDbContext context)
{
    public Task<OrderSummaryDto?> HandleAsync(GetOrderByIdQuery request, CancellationToken cancellationToken)
        => context.Orders.AsNoTracking()
            .Where(o => o.Id == request.OrderId)
            .Select(o => new OrderSummaryDto { /* ... */ })
            .FirstOrDefaultAsync(cancellationToken);
}
```

Loading the entity and mapping in the handler (`repo.GetByIdAsync → entity → Mapper.ToDto(entity)`) is the **anti-pattern this rule eliminates**. So is wrapping the projection in a repository interface — the projection itself IS the read contract. See [docs/cqrs-data-access.md](cqrs-data-access.md) for the full rationale.

---

## 8. Write-side: tracked load → mutate → SaveChanges

The write path inverts the read pattern: load the aggregate (tracked), mutate via domain methods, save. The handler takes `CatalogDbContext` directly — no repository wrapper. Example from [UpdateProduct.cs](../CatalogService/Features/UpdateProduct.cs):

```csharp
public class UpdateProductHandler(CatalogDbContext context, IProductCache cache)
{
    public async Task<bool> HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await context.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);

        if (product is null) return false;
        if (!string.Equals(product.SellerId, request.SellerId, StringComparison.Ordinal)) return false;

        product.UpdateDetails(request.Name, request.Description, request.Price);
        await context.SaveChangesAsync(cancellationToken);

        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
```

### Why tracked

Without tracking, EF doesn't know which properties changed. Calling `SaveChanges` on an untracked entity is a silent no-op (the change tracker has no entry for it).

The handler mutates via a **domain method** (`product.UpdateDetails(...)`) — the domain method enforces invariants (e.g. price > 0). EF detects which scalar properties changed and emits a targeted UPDATE.

### What's actually generated

For a name + price change on a Postgres-backed Product, EF emits:

```sql
UPDATE products SET name = @p0, price = @p1, updated_at = @p2
WHERE id = @p3 AND xmin = @originalXmin;
```

That `AND xmin = @originalXmin` is the concurrency token in action. If another transaction touched this row since we loaded it, the row count is 0 and EF throws `DbUpdateConcurrencyException`.

---

## 9. Read/write code-shape split — the hard rule

The simple rule "always `AsNoTracking` on reads" has a second half: **the read handler should not load an entity at all.** It runs an `IQueryable` that projects to a DTO in EF and returns the DTO directly. Otherwise the handler ends up doing `Mapper.ToDto(entity)` in memory, paying for entity materialization the read path doesn't need.

There are no separate `I*Repository` / `I*ReadStore` interfaces. The split lives in the handler's code shape:

```csharp
// Read handler — AsNoTracking + project to DTO inline
public class GetProductByIdHandler(CatalogDbContext context)
{
    public Task<ProductDto?> HandleAsync(GetProductByIdQuery request, CancellationToken cancellationToken)
        => context.Products.AsNoTracking()
            .Where(p => p.Id == request.ProductId)
            .Select(p => new ProductDto { /* ... */ })
            .FirstOrDefaultAsync(cancellationToken);
}

// Write handler — load tracked, mutate via aggregate method, SaveChanges
public class UpdateProductHandler(CatalogDbContext context, IProductCache cache)
{
    public async Task<bool> HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await context.Products.FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
        if (product is null) return false;
        product.UpdateDetails(request.Name, request.Description, request.Price);
        await context.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
```

The earlier "selective tracking, shared methods preserve tracking deliberately" framing was the wrong trade-off — it saved one method declaration per service at the cost of paying for full entity materialization on every read. The split costs a few lines per handler; the *code shape* itself becomes proof of intent (`AsNoTracking().Select(DTO)` = read; tracked load + mutate + save = write).

---

## 10. Repository pattern — removed in the simplicity refactor

NextAurora previously had `I*Repository` interfaces in every service's Domain folder, with EF implementations in Infrastructure. They were all removed: handlers now take `DbContext` directly.

### Why removed

EF Core's `DbContext` is already the Unit of Work pattern; `DbSet<T>` is already the Repository pattern. The `IFooRepository` wrapper was a layer that added no capability — only the *appearance* of one. The thing the wrapper was actually buying us was the ability to mock handlers in unit tests (`Substitute.For<IFooRepository>` paired with handler logic verification). That justification turned out to be the wrong axis to test on: handlers that touch EF need to be tested with a real database, because the SQL the projection emits, the cartesian-row behavior of collection includes, and the optimistic-concurrency-token behavior are exactly what's load-bearing — and mocks tell you none of those things. So:

1. **Wrapper deleted.** Handlers take `DbContext` directly via constructor injection.
2. **Mocked handler unit tests deleted.** ~23 tests across the four services that used `Substitute.For<IFooRepository>` were replaced or covered by integration tests against Testcontainers (`tests/CatalogService.Tests.Integration` + `tests/OrderService.Tests.Integration`).
3. **Read/write split lives in the handler's code shape**, not in separate interface methods (see §9).

### What survives

Ports kept because consumer substitution actually justifies them: `IEventPublisher` (Wolverine vs. test fake), `IPaymentGateway` (Stripe vs. test fake), `ICatalogClient` (gRPC vs. test fake), `INotificationSender`, `IProductCache` (HybridCache vs. test fake). These pass the rule in CLAUDE.md "Interfaces earn their keep through consumer substitution."

### When you'd revive the repository wrapper

You wouldn't — for any service-shape NextAurora supports. The exception is if a service grew into multi-data-source territory (e.g. read replica routing, event-sourced + relational dual-write), at which point the abstraction lives at the *capability* level (`IOrderReadModelStore` for read-replica vs. primary), not at the per-aggregate level.

---

## 11. N+1, `Include`, projection, `AsSplitQuery`

### 11.1 The N+1 anti-pattern

```csharp
// BAD: 1 query + N queries inside the loop
var orders = await ctx.Orders.ToListAsync();
foreach (var o in orders)
{
    o.Lines = await ctx.OrderLines.Where(l => l.OrderId == o.Id).ToListAsync();
}
```

500 orders → 501 round trips. Database CPU explodes; latency follows.

### 11.2 Two fixes

**Fix A — `Include`:**

```csharp
var orders = await ctx.Orders.Include(o => o.Lines).ToListAsync();
```

One SQL query with a JOIN. Cost: row duplication (Cartesian) — each Order row repeats for each Line.

**Fix B — projection (preferred):**

```csharp
var dtos = await ctx.Orders
    .Select(o => new OrderDto {
        Id = o.Id, Total = o.Total,
        Lines = o.Lines.Select(l => new OrderLineDto {
            ProductId = l.ProductId, Quantity = l.Quantity
        }).ToList()
    })
    .ToListAsync();
```

EF Core 5+ **auto-splits** projected collection navigations: this emits a separate query for `Lines` instead of JOIN-ing them onto Orders, so there are no cartesian rows in the SQL result and no parent column duplication on the wire. You also skip entity materialization — only DTOs allocate. Two independent wins from one operator. Full mechanism in [docs/cqrs-data-access.md "Why projection kills cartesian rows"](cqrs-data-access.md#why-projection-kills-cartesian-rows-the-ef-mechanism).

CLAUDE.md rule:

> No N+1: use `Include` or projection. Never query inside a `foreach` over results from another query.

### 11.3 `AsSplitQuery` — only when measured

When `Include` produces a Cartesian explosion (one Order with 20 lines × 5 navigation collections), `AsSplitQuery()` emits separate queries per collection instead of one giant join:

```csharp
ctx.Orders.AsSplitQuery().Include(o => o.Lines).Include(o => o.Payments).ToListAsync();
```

**Cost:** more round trips (one query per Include), and **transactional inconsistency** is possible — between the parent query and the child query, a concurrent transaction could change a row.

**Rule:** never enable `AsSplitQuery` without profiling. The Cartesian cost has to be measurably worse than the round-trip + isolation cost. CLAUDE.md "Measure before optimizing" explicitly names this one.

---

## 12. `AsNoTrackingWithIdentityResolution` — the `Include` trap

The default behavior of `AsNoTracking()` + `Include(...)` has a subtle bug: **shared related entities get materialized multiple times.**

```csharp
var orders = await ctx.Orders.AsNoTracking().Include(o => o.Customer).ToListAsync();
```

If 500 orders share the same 1 customer, you get **500 separate `Customer` objects** in memory — one per order. Without the change tracker's identity map, EF has nothing to dedupe against.

### Fix

```csharp
var orders = await ctx.Orders
    .AsNoTrackingWithIdentityResolution()
    .Include(o => o.Customer)
    .ToListAsync();
```

This keeps the read-only-no-tracker behavior but enables identity resolution: shared entities get one instance.

**When to use:** any `AsNoTracking + Include` query where the included entity is likely to be shared across multiple parents.

**When to skip:** if you're projecting to a DTO immediately (`.Select(...)`), this is irrelevant — no entities are materialized in the first place.

CLAUDE.md captures this:

> If you must `Include` an entity graph without tracking, use `AsNoTrackingWithIdentityResolution()` (plain `AsNoTracking() + Include` duplicates shared related objects).

---

## 13. Pagination + ordering

Every list endpoint paginates with a **server-side size cap**. Read handlers take `page` + `pageSize` and apply `OrderBy + Skip + Take` inline against the `DbContext`. Example from [GetAllProducts.cs](../CatalogService/Features/GetAllProducts.cs):

```csharp
public async Task<IReadOnlyList<ProductDto>> HandleAsync(GetAllProductsQuery request, CancellationToken cancellationToken)
    => await context.Products.AsNoTracking()
        .OrderBy(p => p.Id).Skip((safePage - 1) * safePageSize).Take(safePageSize)
        .Select(p => new ProductDto { /* ... projection inline ... */ })
        .ToListAsync(cancellationToken);
```

### `OrderBy` is not optional

Without `OrderBy`, SQL doesn't promise stable row order across queries. Page 2 might overlap or skip rows from page 1. Always include an `OrderBy` on at least the PK before `Skip + Take`.

### Server-side caps

Endpoints clamp `pageSize`:

```csharp
private static (int page, int pageSize) ClampPaging(int page, int pageSize) =>
    (page < 1 ? 1 : page, pageSize is < 1 or > 100 ? 50 : pageSize);
```

**Why:** without a cap, a malicious or buggy caller can request `?pageSize=1000000` and OOM the service.

### `OFFSET` is O(N) at large offsets

`Skip(100000)` makes the DB read 100,000 rows then discard them. For deep pagination, **keyset pagination** is correct:

```csharp
// instead of Skip(offset), filter by the last-seen ID
ctx.Orders.OrderBy(o => o.Id).Where(o => o.Id > lastSeenId).Take(pageSize)
```

We don't have a use case yet, but the rule from CLAUDE.md applies:

> Pagination: every list endpoint must paginate with a server-side size cap (≤ 100). Use keyset pagination for large offsets.

---

## 14. Bulk operations: `ExecuteUpdateAsync` / `ExecuteDeleteAsync`

Loading 10,000 rows just to flip `IsDiscounted = true` is 10,000 entities materialized, 10,000 change-tracker entries, 10,000 SQL UPDATEs at `SaveChanges`. **Use bulk operators instead:**

```csharp
// Single UPDATE ... SET ... WHERE ...
await ctx.Products
    .Where(p => p.Category.Name == "Clearance")
    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDiscounted, true));

// Single DELETE FROM ... WHERE ...
await ctx.OutboxRows
    .Where(r => r.PublishedAt < DateTime.UtcNow.AddDays(-30))
    .ExecuteDeleteAsync();
```

**100x to 1000x faster** at scale.

### Caveat — runs outside the change tracker

Don't mix `ExecuteUpdate` with `SaveChanges` on the same entities in the same unit of work — the change tracker still holds the pre-update values and your `SaveChanges` will overwrite the bulk-modified columns.

### Where we'd use it (we don't yet)

- Outbox cleanup (delete published rows older than X)
- Bulk status flips (soft-delete sweeps)
- Backfills

Not in use today. Listed for completeness because it's CLAUDE.md rule #5.

---

## 15. Wolverine transactional outbox integration

This is the most architecturally important EF Core integration in the project. **The entity write and the event publish commit in the same DB transaction — neither happens without the other.**

### 15.1 The dual-write problem

```csharp
// BAD: not atomic
await ctx.SaveChangesAsync();         // Order saved
await bus.PublishAsync(orderEvent);   // Bus down → event lost → PaymentService never runs
```

Without atomicity, "save order" and "publish event" can fail independently. Order saved but PaymentService never hears → customer is charged or not charged unpredictably.

### 15.2 The fix: persist outgoing messages to the same DB

Wolverine 5.36+ ships transactional-outbox helpers built on EF Core. Configured in each event-publishing service's `Program.cs`:

```csharp
builder.Host.UseWolverine(opts =>
{
    var ordersDb = builder.Configuration.GetConnectionString("orders-db")!;
    opts.PersistMessagesWithSqlServer(ordersDb, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    // ... transports + handlers + middleware
});

builder.Services.AddResourceSetupOnStartup();
```

What each line does:

| Call | Effect |
|---|---|
| `PersistMessagesWithSqlServer(cs, "wolverine")` | Wolverine stores outgoing/incoming envelopes in tables under the `wolverine` schema in the same DB |
| `UseEntityFrameworkCoreTransactions()` | Wolverine integrates with EF's transaction so envelope persistence and entity persistence share one transaction |
| `Policies.AutoApplyTransactions()` | Every handler that touches EF auto-wraps in a transaction |
| `Policies.UseDurableOutboxOnAllSendingEndpoints()` | All outgoing messages persist to outbox *before* being dispatched to the bus |
| `AddResourceSetupOnStartup()` | Auto-creates the `wolverine.*` tables on app boot |

### 15.3 What a handler looks like

From [PlaceOrder.cs:144-179](../OrderService/Features/PlaceOrder.cs#L144-L179):

```csharp
// Order saved
var order = Order.Create(request.BuyerId, request.Currency, lines);
await context.Orders.AddAsync(order, cancellationToken);

var @event = new OrderPlacedEvent { /* ... */ };

// PUBLISH BEFORE SAVE — messageContext stages this to wolverine.outgoing_envelopes;
// SaveChangesAsync then flushes the Order row AND the staged envelope in the
// SAME DB transaction.
await messageContext.PublishAsync(@event);
await context.SaveChangesAsync(cancellationToken);
return order.Id;
```

After the handler returns: the transaction commits, both rows persist atomically, a background dispatcher reads from `wolverine.outgoing_envelopes` and sends to RabbitMQ.

### 15.4 Failure modes — all handled

| Failure | Old behavior | New behavior |
|---|---|---|
| Bus publish fails after entity save | Order saved, event lost | Both in outbox-row, dispatcher retries |
| Process crashes between save and publish | Order saved, event lost | Outbox row durable; on restart, dispatcher resumes |
| Bus publish succeeds, save commit fails | Event sent for an order that doesn't exist | Can't happen — both staged in same tx, both commit or neither does |

Full rationale: [docs/performance-and-data-correctness.md "Resolved: transactional outbox via Wolverine"](performance-and-data-correctness.md#resolved-transactional-outbox-via-wolverine).

---

## 16. Optimistic concurrency exception handling

`DbUpdateConcurrencyException` is thrown by `SaveChangesAsync` when the concurrency token check fails. We handle it on two layers.

### 16.1 HTTP path → 409 Conflict

Shared [GlobalExceptionHandler](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs) maps the exception to RFC 7807 ProblemDetails:

```csharp
DbUpdateConcurrencyException => new ProblemDetails
{
    Status = StatusCodes.Status409Conflict,
    Title = "Concurrent modification",
    Detail = "The resource was modified by another request. Refetch and try again.",
    Extensions = { [TraceIdKey] = traceId }
}
```

The caller refetches and decides what to do.

### 16.2 Message path → Wolverine retry

For event handlers, retry is correct: the event is still valid, the handler just needs to reload state and reapply. Wolverine policy in [NextAurora.ServiceDefaults](../NextAurora.ServiceDefaults/Extensions.cs):

```csharp
public static WolverineOptions AddConcurrencyRetry(this WolverineOptions opts)
{
    opts.OnException<DbUpdateConcurrencyException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
    return opts;
}
```

Called from each event-publishing service's `Program.cs`: `opts.AddConcurrencyRetry()`. Three retries with backoff. After exhaustion, the message goes to the DLQ (`wolverine-dead-letter-queue` metric increments).

### 16.3 Concrete saga example

`PaymentCompletedEvent` and `ShipmentDispatchedEvent` both arrive while the order is in `Placed`:

1. Both handlers fetch the order. Both `RowVersion` snapshots are the same.
2. One commits first. Order is now `Paid`, `RowVersion` bumps.
3. The other's `SaveChanges` throws `DbUpdateConcurrencyException`.
4. Wolverine catches, waits 50ms, retries.
5. Retry refetches — now in `Paid`. Calls `MarkAsShipped()` — status guard passes (Paid → Shipped). Save succeeds.

If it races again: 100ms cooldown. Then 250ms. After three: DLQ.

---

## 17. DbContext is not thread-safe — `IDbContextFactory<T>`

Two parallel queries on the same DbContext = boom:

```csharp
// CRASH: InvalidOperationException
await Task.WhenAll(
    ctx.Orders.ToListAsync(),
    ctx.Payments.ToListAsync()
);
```

The DbContext holds a single connection, a single change-tracker, and a single query pipeline. Concurrent access corrupts all three.

### The fix: `IDbContextFactory<T>`

Register the factory:

```csharp
services.AddDbContextFactory<OrderDbContext>(options =>
    options.UseSqlServer(connectionString));
```

Then in code:

```csharp
public async Task<DashboardData> Build(IDbContextFactory<OrderDbContext> factory, Guid userId)
{
    // Each task gets its own DbContext, its own change tracker, its own connection
    await using var ctx1 = await factory.CreateDbContextAsync();
    await using var ctx2 = await factory.CreateDbContextAsync();
    var ordersTask = ctx1.Orders.Where(o => o.BuyerId == userId).ToListAsync();
    var paymentsTask = ctx2.Payments.Where(p => p.BuyerId == userId).ToListAsync();
    await Task.WhenAll(ordersTask, paymentsTask);
    return new DashboardData(ordersTask.Result, paymentsTask.Result);
}
```

CLAUDE.md rule:

> `DbContext` is not thread-safe: parallel queries (`Task.WhenAll`) require `IDbContextFactory<T>` — one context per task. The scoped per-request context handles only sequential work.

**Today we don't fan out queries anywhere** — the audit confirmed it. The moment we do (e.g., a dashboard endpoint loading orders + payments + shipments in parallel), this rule applies.

---

## 18. Connection lifetime

Connections are pooled (Postgres typically 100/instance, SQL Server 100/instance default). Each open connection holds a slot.

### The anti-pattern

```csharp
public async Task<DataDto> SomeHandler()
{
    var data = await ctx.Foo.FirstAsync();
    await Task.Delay(200);                    // ← BAD: connection sits idle for 200ms
    var external = await httpClient.GetAsync(...);  // ← BAD: same problem
    return new DataDto { ... };
}
```

The DbContext is scoped per request → the connection is held for the *entire request lifetime*, including any HTTP call you make mid-handler. At even modest concurrency, the pool exhausts.

### The pattern

```csharp
public async Task<DataDto> SomeHandler()
{
    var data = await ctx.Foo.FirstAsync();
    var dataCopy = new SomeShape(data);          // copy what you need

    // Now do the slow unrelated work — DbContext isn't being used, but it's still scoped
    // and still holding a pooled connection slot until the scope disposes at request-end.
    // If you genuinely need to free the slot mid-request, open a sub-scope and dispose
    // it before the slow await.

    var external = await httpClient.GetAsync(...);
    return new DataDto { ... };
}
```

CLAUDE.md rule:

> DB connection hold time: open → query → dispose. Don't `await` unrelated work (HTTP calls, message publishes) while a connection is open.

In practice with scoped DbContexts, this means **finish all DB work first**, then do external I/O. If a handler genuinely needs to interleave, use a manual sub-scope.

---

## 19. Dapper escape hatch

EF Core handles ~95% of our patterns well. The remaining 5%:

- Provider-specific SQL (Postgres `ILIKE`, full-text search; SQL Server `MERGE`, hint syntax)
- Hot paths where profiling shows EF as the bottleneck
- Reporting / aggregate queries where SQL is the natural expression and LINQ obscures intent

For these, **Dapper is the sanctioned escape hatch — not a peer abstraction.** Pattern:

```csharp
public sealed class ProductReportRepository(CatalogDbContext ctx)
{
    public async Task<IReadOnlyList<TopSellerRow>> GetTopSellersAsync(
        DateOnly since, int limit, CancellationToken ct)
    {
        // Same connection EF already opened for this scope — Dapper participates in any
        // ambient EF transaction. No second pool slot consumed.
        var connection = ctx.Database.GetDbConnection();

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

### When to use Dapper

- Provider-specific SQL that doesn't translate cleanly
- Profiling proves EF is the hot-path bottleneck
- Aggregations that read more naturally as SQL

### When NOT to use Dapper

- Straightforward CRUD reads — EF projection wins on type-safety
- Writes — Dapper bypasses concurrency tokens, domain validation, and the outbox
- Avoiding learning EF projection syntax
- Speculative perf rewrites without measurement

Full guidance: [docs/performance-and-data-correctness.md "Decision: when to reach past EF Core (Dapper escape hatch)"](performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch).

CLAUDE.md hard rule:

> Dapper is the sanctioned escape hatch from EF, not a peer abstraction. Always use `ctx.Database.GetDbConnection()` so Dapper shares the EF connection and any ambient transaction.

---

## 20. HybridCache invalidation in the write path

CatalogService uses HybridCache (L1 in-process MemoryCache + L2 Redis) for `GetProductByIdQuery` reads. **Every write that mutates a Product must invalidate the cache in the same handler.**

From [UpdateProduct.cs](../CatalogService/Features/UpdateProduct.cs):

```csharp
public async Task<bool> HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
{
    var product = await context.Products
        .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);
    if (product is null) return false;
    if (!string.Equals(product.SellerId, request.SellerId, StringComparison.Ordinal))
        return false;

    product.UpdateDetails(request.Name, request.Description, request.Price);
    await context.SaveChangesAsync(cancellationToken);

    // Invalidate AFTER the save so a concurrent reader can't repopulate the cache with
    // the pre-update DTO between our invalidate and our save.
    await cache.InvalidateAsync(request.ProductId, cancellationToken);
    return true;
}
```

### Ordering matters

**Invalidate AFTER save**, not before. If you invalidate first:
1. You invalidate the cached value
2. A concurrent reader misses, hits the DB, repopulates the cache with the *old* value
3. You save your new value to the DB
4. The cache now has the old value; readers see stale data until TTL

Invalidating after save closes that window. There's still a tiny race window between save and invalidate, but the 5-minute TTL is the safety net.

CLAUDE.md rule:

> Cache invalidation in the write path: if a handler mutates a cached entity, it must invalidate or update the cache in the same handler — not "later" or "via TTL".

Full design: [docs/performance-and-data-correctness.md "Decision: distributed read caching with HybridCache"](performance-and-data-correctness.md#decision-distributed-read-caching-with-hybridcache).

---

## 21. Hard rules summary (CLAUDE.md)

Quick-reference. All from [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules).

| # | Rule | Why |
|---|---|---|
| 1 | EF Core reads: `AsNoTracking()` + projection (`.Select(...)` to DTO) | No tracker overhead, smaller SQL, no entity graph allocation |
| 2 | No N+1 — use `Include` or projection, never query inside a `foreach` over query results | 501 queries vs 1 |
| 3 | `await` everywhere on request paths, never `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`. Propagate `CancellationToken` everywhere | Thread-pool starvation; client disconnects don't waste work |
| 4 | Every list endpoint paginates with a server-side size cap (≤ 100). Keyset for large offsets | OOM prevention; `OFFSET` is O(N) |
| 5 | `ExecuteUpdateAsync` / `ExecuteDeleteAsync` for bulk ops | 100-1000x faster than load + change tracker + SaveChanges |
| 6 | Every updatable aggregate has a concurrency token (`xmin` or `RowVersion`) | Last-write-wins is not acceptable |
| 7 | Entity write + outbox-row write commit in the same transaction | Dual-write problem solved |
| 8 | Parallel queries need `IDbContextFactory<T>` (one context per task) | DbContext isn't thread-safe |
| 9 | Structured logging with message templates, never string concatenation | Skips allocation when log level is filtered out; produces queryable fields |
| 10 | No logging in tight loops — summarize | I/O bottleneck under load |
| 11 | Open → query → dispose. Don't `await` unrelated I/O with connection open | Connection pool exhaustion |
| 12 | Cache invalidation in the write path — same handler, not via TTL | Bounded staleness only |
| 13 | Migrations are immutable once applied anywhere. Destructive changes need multi-step plan | Snapshot/history drift; deploy outages |
| 14 | Measure before optimizing — `BenchmarkDotNet`, `dotnet-counters`, `EF.ToQueryString()` | Most "optimizations" without a profiler make things worse |

Plus the Dapper escape-hatch rule:

> Dapper is the sanctioned escape hatch from EF Core, not a peer abstraction. Use `ctx.Database.GetDbConnection()`. Writes always go through aggregates + EF (Dapper bypasses concurrency tokens, domain validation, and the outbox).

---

## 22. Crib sheet

A condensed walkthrough of the key EF Core decisions in this codebase, each mapped to a section above. Useful as a refresher.

### "How do you handle concurrency in EF Core?"

> Every updatable aggregate has an optimistic concurrency token. Postgres uses the system `xmin` column mapped as a shadow property — no schema change needed. SQL Server uses a `byte[] RowVersion` shadow column added by migration. EF includes the token in the UPDATE's WHERE clause; if the row was touched since we loaded it, the UPDATE matches zero rows and EF throws `DbUpdateConcurrencyException`. HTTP commands get 409 Conflict via the global exception handler; Wolverine event handlers retry three times with backoff before DLQing.

### "How do you handle the dual-write problem?"

> Wolverine's transactional outbox. The entity write and the outgoing message persist to a `wolverine` schema in the same DB, in the same EF transaction. After the handler returns, both commit together — neither happens without the other. A background dispatcher reads from `wolverine.outgoing_envelopes` and sends to RabbitMQ with retry. So "order saved but event lost" can't happen.

### "Repository pattern over EF Core — isn't that redundant?"

> Yes — and we removed it. EF's `DbContext` IS the Unit of Work pattern; `DbSet<T>` IS the Repository pattern. The `IFooRepository` wrapper added a layer without adding capability. The only thing it was buying us was the ability to mock handlers in unit tests, and mocked handler tests turned out to be the wrong axis to test on (the SQL, the cartesian-row behavior, the concurrency-token behavior are exactly what's load-bearing — and mocks tell you none of those things). Now handlers take `DbContext` directly; correctness of EF-touching code paths is proven by integration tests against Testcontainers. See CLAUDE.md "Data access: DbContext directly, no repository wrappers" + §10 above.

### "AsNoTracking everywhere?"

> Almost. Read handlers use `AsNoTracking()` + inline `.Select` projection straight into the DTO — no tracker overhead, smaller SQL, no entity allocations. Write handlers do the opposite: they load the aggregate tracked (no `AsNoTracking`), mutate via state-transition methods (`MarkAsPaid`, `AdjustStock`), and call `SaveChangesAsync`. The split lives in the handler's code shape — `AsNoTracking().Select(DTO)` is a read; tracked load + mutate + save is a write. The previous shape (shared `GetByIdAsync` keeping tracking on for both paths) was retired with the repository wrappers.

### "What's the catch with AsNoTracking + Include?"

> Shared related entities get duplicated. 500 orders sharing one customer → 500 separate Customer instances in memory because there's no identity map to dedupe against. Fix: `AsNoTrackingWithIdentityResolution()` — keeps the read-only behavior but enables dedup. Irrelevant if you're projecting to a DTO (no entity gets materialized).

### "Postgres vs SQL Server — why both?"

> Polyglot persistence is microservices' data-autonomy argument made visible. They surface real EF provider differences side-by-side (xmin vs RowVersion is the cleanest example). And mixing mirrors the enterprise reality of inheriting both stacks. The "Postgres for read-heavy / SQL Server for transaction-heavy" rationale in our docs is slightly overstated — both engines handle either workload well. A production decision would weigh licensing, team expertise, and existing infrastructure more than workload fit.

### "How do you handle bulk updates?"

> `ExecuteUpdateAsync` / `ExecuteDeleteAsync` — single SQL statement, bypasses the change tracker entirely. 100-1000x faster than loading entities, mutating, and SaveChanges. Caveat: it runs outside the change tracker, so don't mix it with SaveChanges on the same entities in the same unit of work — you'll get stale tracked data.

### "What if EF Core isn't fast enough for a specific query?"

> Dapper is the sanctioned escape hatch. We reach for it only when (a) the SQL is provider-specific and doesn't translate cleanly, (b) profiling proves EF is the bottleneck on a hot path, or (c) the query is a SQL aggregation where LINQ obscures intent. Always use `ctx.Database.GetDbConnection()` so Dapper shares the EF connection and ambient transaction — never open a separate one (that'd double the connection pool pressure and lose transaction sharing). Writes always go through aggregates + EF — Dapper bypasses concurrency tokens, domain validation, and the outbox.

### "Migrations in production?"

> Not in-process. `MigrateDatabaseAsync` is dev-only — gated on `IsDevelopment()`. With multiple replicas behind a load balancer, all replicas would race to apply migrations on startup. Production needs migrations as a separate pre-deploy step before app pods take traffic. Tooling exists; deploy automation doesn't (it's on the open-issues list). Hard rule: migrations are immutable once applied anywhere — including dev. Destructive changes need a multi-step plan: deploy reader code that ignores the column → wait one release → drop the column in a follow-up migration.

### "How do you prevent N+1?"

> Projection over Include. `.Select(new Dto { Lines = o.Lines.Select(l => new LineDto { ... }) })` triggers EF Core's auto-split behavior for the projected collection navigation — separate SQL queries for the parent and the children, no JOIN, no cartesian rows over the wire. Plus you skip entity materialization entirely. `Include` + entity materialization forces a single JOIN, which produces cartesian rows in SQL *and* duplicate parent objects in memory (the latter is what `AsNoTrackingWithIdentityResolution` fixes; the former needs `AsSplitQuery`). The projection rule wins on both axes at once. Full mechanism breakdown in [docs/cqrs-data-access.md "Why projection kills cartesian rows"](cqrs-data-access.md#why-projection-kills-cartesian-rows-the-ef-mechanism).

### "How do you handle a connection holding the pool slot too long?"

> Open → query → dispose. With scoped DbContexts, the connection's held for the whole request — so any `await` on unrelated I/O (HTTP, message publish) while still holding the DbContext eats a pool slot for that duration. Pattern: finish all DB work, copy what you need, then do the slow external work. If a handler genuinely needs to interleave, open a sub-scope and dispose it before the slow await.

---

## See also

- [CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules) — the canonical hard-rule list
- [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) — full rationale per decision (concurrency tokens, AsNoTracking, outbox, HybridCache, Dapper, concurrency hazards)
- [docs/cqrs-data-access.md](cqrs-data-access.md) — handler inventory, read/write method split (the rule), per-architecture canonical shape
- [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md) — deeper EF performance material (compiled queries, query filters, interceptors)
