# Modern .NET 10 / C# 13 Features in NextAurora

A reference of the modern .NET features actively in use across this project — what they are, where they live in the codebase, and why we picked them.

Every entry is anchored to a real file. If you can't point at code, you don't actually use the feature.

---

## 1. HybridCache (Microsoft.Extensions.Caching.Hybrid 10.5.0)

**What it is**: First-party .NET 10 caching abstraction that combines L1 (in-process `IMemoryCache`) and L2 (any `IDistributedCache` — we use Redis) with **stampede protection** baked in. Concurrent misses for the same key fire the factory function once; the other waiters get the result.

**Where**: [CatalogService.Infrastructure/Caching/HybridProductCache.cs](../CatalogService/Infrastructure/Caching/HybridProductCache.cs), wired in [Program.cs](../CatalogService/Program.cs).

**Why**: Before HybridCache, you'd hand-roll L1+L2 with `IMemoryCache` plus your own coordination logic, plus your own stampede protection (usually a `SemaphoreSlim` per key — error-prone). HybridCache replaces ~50 lines of plumbing per cached entity with a one-line `GetOrCreateAsync`.

**Gotcha**: HybridCache 10.x has **no cross-replica L1 backplane**. The dotnet/extensions proposal for one was closed as "not ready for implementation." So when replica A invalidates an entry, replicas B/C keep serving stale L1 values until their local TTL expires. Mitigations: drop L1 TTL to 60s, or migrate to **FusionCache** which has a Redis pub/sub backplane. Documented in [STATUS.md](STATUS.md#if-we-deploy-multi-replica-hybridcache-l1-cross-replica-invalidation).

**When to use it**: single-replica or sticky-session services. Multi-replica with strong consistency requirements is where FusionCache (with its Redis pub/sub backplane) becomes the right call.

---

## 2. Microsoft.AspNetCore.OpenApi (10.0.2) + Scalar

**What it is**: Microsoft's built-in OpenAPI document generator that **replaced Swashbuckle** as the ASP.NET Core web template default in .NET 9. Reads endpoint signatures + minimal-API metadata and emits the spec at build/runtime. Source-generated when AOT is on.

**Where**: Every service's `Program.cs`. Example: [CatalogService/Program.cs](../CatalogService/Program.cs#L131-L133).

```csharp
builder.Services.AddOpenApi();
// ...
app.MapOpenApi();                                   // /openapi/v1.json
app.MapOpenApi("/openapi/{documentName}.yaml");     // /openapi/v1.yaml
app.MapScalarApiReference();                        // /scalar/v1 — interactive UI
```

**Why we picked Scalar over Swagger UI**: Scalar (`Scalar.AspNetCore` 2.14.11) reads the same OpenAPI doc, but the UI is dramatically better — cleaner layout, dark mode, real fetch try-it-out (no iframe weirdness), client-code generation in 8 languages. Integrates cleanly with `Microsoft.AspNetCore.OpenApi`'s native generation.

**Live example**: https://catalog-api-demo.fly.dev/scalar/v1

**Context**: Swashbuckle remains widely used but the .NET template moved off it in 9. `Microsoft.AspNetCore.OpenApi` is first-party, integrates with source generators for AOT scenarios, and stays in lockstep with new ASP.NET Core metadata. Scalar is the UI layer that pairs with it.

---

## 3. Primary constructors (C# 12)

**What it is**: Constructor parameters declared on the class declaration itself, in scope for the entire class body. Eliminates the boilerplate of capturing constructor params into fields.

**Where**:
- [CatalogService.Infrastructure/Data/CatalogDbContext.cs](../CatalogService/Infrastructure/Data/CatalogDbContext.cs#L28) — `public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)`
- [NextAurora.ServiceDefaults/GlobalExceptionHandler.cs](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs) — `public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler`

**Why**: For DI-heavy classes that just pass parameters through to base or store them as fields, primary constructors save 3-5 lines per class. Especially nice for DbContexts and exception handlers.

**Gotcha**: The parameter is in scope but isn't a real field. You can't reference `options` from an instance method unless you explicitly capture it (`private readonly DbContextOptions _options = options;`). So it's best for pass-through-to-base, less great when you need the value later.

**Rule of thumb**: use primary constructors for DbContexts and exception handlers — pure DI pass-throughs. When the constructor parameter needs to be read from instance methods, declare the field explicitly so the intent is obvious.

---

## 4. Collection expressions (C# 12)

**What it is**: `[]` literal syntax that works for any collection type the compiler can target — arrays, `List<T>`, `IEnumerable<T>`, `ImmutableArray<T>`, `Span<T>`, custom types with the right marker. Plus the `..spread` operator.

**Where**:
- [CatalogService/Domain/Category.cs:20](../CatalogService/Domain/Category.cs#L20) — `public List<Product> Products { get; private set; } = [];`
- [ShippingService/Domain/Shipment.cs:43](../ShippingService/Domain/Shipment.cs#L43) — `public List<TrackingEvent> TrackingEvents { get; private set; } = [];`
- [NextAurora.Contracts/Events/OrderPlacedEvent.cs:10](../NextAurora.Contracts/Events/OrderPlacedEvent.cs#L10) — `public List<OrderLineContract> Lines { get; init; } = [];`
- Tests pass empty collections via `Lines = []`

**Why**: Shorter than `new List<T>()`, type-agnostic so refactoring property types is one line of change. The compiler picks the optimal underlying allocation.

**Net effect**: collection expressions look like syntactic sugar but they target whatever collection type the property declares. Refactoring `List<T>` to `IReadOnlyList<T>` or `ImmutableArray<T>` doesn't require changing the initializer.

---

## 5. Minimal APIs + Asp.Versioning.Http (10.0.0)

**What it is**: Endpoint registration without MVC controllers — direct `MapGet`/`MapPost`/etc. on routes — combined with `Asp.Versioning.Http` for URL-segment versioning (`/api/v1/...`).

**Where**: Every service's `Endpoints/*.cs`. Example: [CatalogService.Api/Endpoints/CatalogEndpoints.cs](../CatalogService/Endpoints/CatalogEndpoints.cs).

The `MapV1ApiGroup(tag, resource)` helper in [NextAurora.ServiceDefaults/Extensions.cs](../NextAurora.ServiceDefaults/Extensions.cs) is the canonical entry — returns a `RouteGroupBuilder` rooted at `/api/v1/resource` with version + OpenAPI tag applied in one call. Stops drift across services.

**Why minimal APIs over Controllers**:
- Faster startup (no MVC pipeline)
- Less ceremony — no `[FromRoute]` `[FromBody]` `[ApiController]` attributes
- Better OpenAPI integration in .NET 9+
- Endpoint filters (`AddEndpointFilter`) replace action filters more cleanly

**Why Asp.Versioning.Http** (not Microsoft.AspNetCore.Mvc.Versioning, which was MVC-only): It's the same maintainer's modern minimal-API-compatible version. URL-segment versioning is required (`AssumeDefaultVersionWhenUnspecified = false`) so callers can't accidentally hit v1 expecting v2.

**Why URL versioning over header versioning**: Stripe, GitHub, and AWS all use URL versioning. URLs are visible in logs and dashboards, cacheable, debuggable from a browser. The "header versioning is more RESTful" argument is academic.

---

## 6. System.Threading.RateLimiting

**What it is**: Built-in rate limiting middleware introduced in .NET 7, matured through 10. Strategies include `FixedWindow`, `SlidingWindow`, `TokenBucket`, `Concurrency`.

**Where**: [CatalogService/Program.cs:27-37](../CatalogService/Program.cs#L27-L37) — fixed-window limiter for product search (30 requests / 10s).

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("search", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromSeconds(10);
        limiter.QueueLimit = 0;
    });
});
```

**Why**: Pre-`System.Threading.RateLimiting`, every team wrote their own — usually with semaphores and a `Dictionary<string, Counter>`, with bugs. The built-in is performant, integrates with endpoint metadata (`.RequireRateLimiting("search")`), and is policy-based so you can A/B test limits.

**Net effect**: rate limiting used to be a hand-rolled middleware in every project, usually with off-by-one bugs in the windowing. The first-party version is policy-named, endpoint-attached via `.RequireRateLimiting("policy-name")`, and free.

---

## 7. `IExceptionHandler` + `ProblemDetails` (RFC 7807)

**What it is**: `IExceptionHandler` interface (.NET 8+) for centralized exception → HTTP response mapping. `ProblemDetails` is the RFC 7807 standard error format (status + title + detail + traceId).

**Where**: [NextAurora.ServiceDefaults/GlobalExceptionHandler.cs](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs) — single handler maps `ValidationException` → 400, `DbUpdateConcurrencyException` → 409, etc. Registered in `Extensions.cs`.

**Why**: Pre-.NET 8 you'd use `UseExceptionHandler` with a lambda or write middleware. `IExceptionHandler` is DI-friendly (constructor-inject your logger) and composable — you can chain multiple handlers.

`ProblemDetails` matters because: every error response has the same shape (`type`, `title`, `status`, `detail`, `traceId`), clients can parse it consistently, and you never leak internal stack traces or entity IDs.

**Why it matters**: in microservice systems where every service returned errors in a different shape, debugging was a nightmare. RFC 7807 gives uniform error contracts plus a trace ID that links the response to your distributed traces.

---

## 8. EF Core 10 — modern patterns we use

**`AsNoTracking()` + projection on every read**: [CatalogService/Features/GetProductById.cs](../CatalogService/Features/GetProductById.cs) (`.AsNoTracking().Where(...).Select(p => new ProductDto { ... })`). Read handlers project to the DTO inline — no `Include`, no entity materialization, no change-tracker entry. SQL emits only the columns the DTO needs.

**`ExecuteUpdateAsync` / `ExecuteDeleteAsync`**: Bulk operations as single SQL statements. Example in [tests](../tests/CatalogService.Tests.Integration/ProductCachingTests.cs#L75): `await db.Products.Where(p => p.Id == productId).ExecuteDeleteAsync();`.

**`xmin` concurrency token (Postgres-specific)**: [CatalogService.Infrastructure/Data/CatalogDbContext.cs:63](../CatalogService/Infrastructure/Data/CatalogDbContext.cs#L63) — shadow property bound to Postgres's system column. EF includes `WHERE xmin = N` on every UPDATE. Second writer's WHERE matches zero rows → `DbUpdateConcurrencyException` → handler returns 409. No schema column needed.

**`HasData()` declarative seeding**: [CatalogService.Infrastructure/Data/CatalogDbContext.cs:73-109](../CatalogService/Infrastructure/Data/CatalogDbContext.cs) — seed data lives in the model config; `dotnet ef migrations add` generates the INSERT statements automatically; deterministic GUIDs and dates so the migration is reproducible.

**Migrations as a contract**: `__EFMigrationsHistory` table in the DB tracks applied migrations; `Migrate()` applies only the new ones. Compare to the ADO.NET + sprocs world where missed migrations are invisible until traffic finds them.

Full deep-dive: [docs/ef-core.md](ef-core.md).

---

## 9. `ForwardedHeaders` + `KnownIPNetworks` (.NET 10 rename)

**What it is**: Middleware that trusts proxy-forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) so ASP.NET Core sees the original client IP + scheme instead of the proxy's.

**Where**: [CatalogService.Api/Program.cs](../CatalogService/Program.cs) — DemoMode-gated, configured before `Build()` and used early in the middleware pipeline.

**Why**: PaaS hosts (Fly.io, AWS App Runner, Azure App Service) terminate TLS at the edge and forward HTTP to your container. Without trusting `X-Forwarded-Proto: https`, ASP.NET Core sees the request as HTTP, and OpenAPI emits `http://...` server URLs — which the browser blocks as mixed content when Scalar tries to make try-it-out requests.

**.NET 10 gotcha**: `KnownNetworks` was renamed to **`KnownIPNetworks`** in .NET 10. If you copy-paste a sample from a .NET 8 blog post, you'll get an obsoletion error. Discovered the hard way during the Fly deploy.

**The pattern**: every PaaS deploy hits this — TLS terminates at the edge so the app sees HTTP. `ForwardedHeaders` middleware is mandatory in that topology. In .NET 10 specifically, `KnownNetworks` got renamed to `KnownIPNetworks`, so older samples don't compile.

---

## 10. OpenTelemetry-first observability (1.15.x)

**What it is**: OpenTelemetry SDK is the .NET community's converged answer for metrics, tracing, and logs. Exports to any OTLP receiver (Aspire dashboard locally, Datadog/New Relic/Honeycomb in production).

**Where**: [NextAurora.ServiceDefaults/Extensions.cs](../NextAurora.ServiceDefaults/Extensions.cs) — registers metrics + tracing + log providers globally. Every service inherits.

**Why** (vs Serilog + Seq + Jaeger, which was the .NET-typical stack 3 years ago): one library handles all three signals; vendor-neutral; supports Activity baggage for correlation/user/session ID propagation across services.

Business counters (`orders.placed`, `payments.processed`, `shipments.dispatched`, `notifications.sent`) are declared on the `Meter("NextAurora")` in the handlers that emit them.

**Structured logging templates** (not string interpolation):
```csharp
logger.LogInformation("User {UserId} placed order {OrderId}", userId, orderId);
```
Lets log aggregators index by `UserId` and `OrderId` — searchable, not text-grep-able. CLAUDE.md mandates this; the `LoggerMessage` source generator gets used for hot paths.

**Context**: a few years ago the typical .NET observability stack was Serilog + Seq + Jaeger plus a metrics library — three separate things, three separate sinks. OpenTelemetry collapses it to one SDK, one export protocol, three signals. Aspire dashboard locally, OTLP-anything in production.

---

## 11. Wolverine — covers MediatR + MassTransit (both gone commercial)

**What it is**: Single library that handles in-process CQRS (the MediatR slot), message-broker dispatch (the MassTransit slot), AND the transactional outbox pattern across both.

**Where**: Every service's `Program.cs` has `builder.Host.UseWolverine(opts => ...)`. Example: [CatalogService/Program.cs:39](../CatalogService/Program.cs#L39).

**Why we picked Wolverine over MediatR + MassTransit**:
- **MediatR went commercial in 2024** (sponsorware via Jimmy Bogard's company). v12 is the last free version.
- **MassTransit v9 announced commercial licensing in April 2025** for production use (effective Q1 2026).
- Wolverine sidesteps both, and the **transactional outbox** is built in — `PersistMessagesWith{SqlServer|Postgresql}` + `AutoApplyTransactions` + `UseDurableOutboxOnAllSendingEndpoints`. Three lines wire up the dual-write-problem solver.

**Outbox is the under-appreciated win**. The MediatR/MassTransit replacement framing focuses on licensing, but the technical case for Wolverine is that the transactional outbox is built in — not a separate library, not a pattern to remember to apply. The classic "message lost in a DB crash mid-handler" failure mode is structurally impossible because the message persist + entity write share a transaction.

Full rationale: [docs/project-decisions.md "Wolverine — covers MediatR + MassTransit, both now commercial"](project-decisions.md#13-wolverine--covers-mediatr--masstransit-both-now-commercial).

---

## 12. Research-track (not yet shipped, but watching)

Items researched but deliberately not used in NextAurora — a record of the road not taken and why.

**Native AOT**: GA for ASP.NET Core minimal APIs in .NET 8, ongoing improvements through 10. Reduces cold start ~80% (1.5-3s → 200-500ms), smaller container images, smaller memory. NOT used here because EF Core's migration tooling relies on reflection — workaround is compiled models + `dotnet ef migrations bundle`, which is a real workflow change. AOT shines on Azure Functions Consumption / Cloud Run / Lambda where cold start dominates p99.

**FusionCache**: Drop-in replacement for HybridCache when you need a cross-replica L1 backplane (HybridCache 10.x doesn't have one). Same `IDistributedCache` + memory-cache shape, plus Redis pub/sub backplane. Half-day migration when we actually need multi-replica consistency.

**SignalR backplane via Redis**: Not in NextAurora because no realtime UI. If we ever add live order tracking, that's the path.

**Net effect**: Native AOT is the most consequential of these. It shines on thin Functions / Lambda / Cloud Run workloads where cold start dominates p99 latency. It collides with anything reflection-heavy — EF Core migration discovery being the canonical example. The decision is workload-by-workload, not a blanket "use AOT."

---

## What "modern .NET" actually means in this codebase

Of the features above, the truly .NET-10-specific bits are narrow: **HybridCache**, the `KnownNetworks` → `KnownIPNetworks` rename, and ongoing Native AOT improvements. Most of what makes a codebase feel modern is the **C# 12 / .NET 8 baseline used consistently** — primary constructors, collection expressions, `IExceptionHandler`, `System.Threading.RateLimiting`, first-party OpenAPI — rather than any single headline feature.

The ecosystem-level shifts are arguably more impactful than language-level ones:
- **Wolverine** replacing the MediatR + MassTransit pair (both now commercial) — single library, transactional outbox built in
- **OpenTelemetry** replacing Serilog + Seq + Jaeger as the default observability stack — one SDK, three signals
- **Scalar** replacing Swagger UI — paired with first-party `Microsoft.AspNetCore.OpenApi`
- **Native AOT** as a real production option for thin workloads (not a research toy anymore)

Each of those decisions is rationalized in [project-decisions.md](project-decisions.md) with the alternatives considered.
