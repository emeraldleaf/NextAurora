# NextAurora - Claude Code Project Instructions

> **New session?** Read [docs/STATUS.md](docs/STATUS.md) first — it's the cross-session entry point: where the project is, what to do next, and links into the deeper docs.

## Project Overview

NextAurora is a .NET 10 microservices e-commerce platform using Aspire, Azure Service Bus, gRPC, EF Core, and Blazor. It follows DDD, CQRS, and event-driven architecture.

## Architecture Principles

### SOLID

- **Single Responsibility**: Each class has one reason to change. Handlers handle one command/query. Processors handle one event type. Do not mix concerns.
- **Open/Closed**: Use abstractions (interfaces, base classes) so behavior can be extended without modifying existing code. New event types = new handler classes, not new branches in existing processors.
- **Liskov Substitution**: All interface implementations must fully honor the contract. Repository implementations must handle all methods.
- **Interface Segregation**: Keep interfaces focused. Separate read/write repository interfaces if consumers only need one. Do not force unused dependencies.
- **Dependency Inversion**: Always depend on abstractions (interfaces), never on concrete implementations. Domain and Application layers must never reference Infrastructure.
- **Interfaces earn their keep through *consumer substitution*, not "future swap"**: a port/adapter interface (`IFooRepository`, `IFooGateway`, `IEventPublisher`, `IFooSender`, `IFooResolver`) is justified when at least one of: **(a)** it's substituted by tests today (NSubstitute mock, fake, in-memory double — verify with `grep "Substitute.For<IFoo"`), **(b)** there are two or more concrete implementations registered against it today (dev + prod adapter, multi-tenant variants), or **(c)** a second implementation is on a *concrete* near-term roadmap item — not "we might want X someday." If none of (a)/(b)/(c) holds, the interface is speculative coupling and should be deleted; the handler can take the concrete class directly. Cross-reference: every repository in NextAurora today (`IOrderRepository`, `IPaymentRepository`, `IShipmentRepository`, `IProductRepository`), every event publisher, and `IPaymentGateway` qualify under (a). Past failures hit (a): `IRecipientResolver`/`StubRecipientResolver` in NotificationService had no test substitution, no second impl, only "in production this would..." — deleted.

### Domain-Driven Design

- **Rich Domain Entities** (when warranted): Entities that are *persisted* and have *non-trivial, observable invariants* must enforce them — state changes go through methods, never public setters, with factory methods (static `Create()`) that validate inputs. **The pattern only earns its keep when someone observes the invariant.** If the entity is in-memory, single-use, and discarded after the handler returns, skip the aggregate shape entirely — inline the validation, or use a FluentValidation rule on the command. A factory + private setters + status enum that nothing reads is ceremony, not architecture. NotificationService is the canonical "no aggregate" example: stateless event-to-email pump, no persistence, no domain rules worth protecting.
- **Value Objects**: Use value objects for concepts like Money (amount + currency), Quantity (non-negative int). They enforce rules at construction.
- **Aggregates**: Each aggregate root controls access to its children. Do not expose mutable collections. Add methods like `AddLine()` instead of exposing `List<T>`.
- **Domain Events**: State changes that affect other bounded contexts should raise domain events.
- **Layer Dependencies**: Domain -> nothing. Application -> Domain. Infrastructure -> Domain + Application. Api -> all layers (composition root). **A service with no domain entities doesn't need a Domain project** — ports (`I*Sender`, `I*Resolver`) live in `Application/Interfaces/` instead. NotificationService is the precedent: 3 projects (Api/Application/Infrastructure), no Domain.

### Security Requirements

- **Authentication**: All non-public endpoints must use `.RequireAuthorization()`. JWT Bearer authentication.
- **Authorization**: Users can only access their own resources. Validate `buyerId` matches authenticated user.
- **Input Validation**: All commands must have FluentValidation validators. Validate at the API boundary before reaching handlers.
- **Error Handling**: Never expose internal state, stack traces, or entity IDs in API responses. Log details server-side, return generic errors with correlation IDs to clients.
- **HTTPS**: Enforce HTTPS redirection in production.
- **CORS**: Explicit CORS policy allowing only known frontend origins.
- **Rate Limiting**: Applied to search and payment endpoints at minimum.

## Project Structure

**Per-service shape is calibrated to per-service complexity.** Two patterns coexist in this
repo on purpose, with one rule per pattern:

### Clean Architecture — CatalogService only

The largest service (~2k LOC, multiple aggregates, caching, gRPC, optimistic concurrency,
integration tests). The four-project split *earns its keep*: enough aggregates that the
build-time layer enforcement protects against real violations, and the size makes
"find every handler" / "find every repository" a worthwhile axis to organize on.

```
CatalogService/
  CatalogService.Domain/          # Entities, value objects, enums, interfaces (no dependencies)
  CatalogService.Application/     # Commands, queries, validators, handlers, mappers
  CatalogService.Infrastructure/  # EF Core, repositories, caching, messaging
  CatalogService.Api/             # Endpoints, middleware, DI composition root, gRPC service
```

### Vertical Slice Architecture — Order/Payment/Shipping/Notification

Smaller services (~250–1400 LOC, ≤2 aggregates each). The Clean Architecture project split
costs more than it pays at this scale — four csprojs, cross-project `using` statements, and
"find everything related to PlaceOrder" becoming a multi-project search. Collapsed to one
project per service, organized by *feature* instead of *kind*.

```
ServiceName/
  Features/                       # One file per use case: command/query record + validator + handler co-located.
                                  # Saga event-handler classes also live here (they're features too).
  Domain/                         # Shared aggregates, value objects, enums, ports (interfaces consumed by features).
  Infrastructure/                 # EF Core (with /Data/ + /Migrations/), repositories, gateways, DI composition.
  Endpoints/                      # Minimal-API endpoint registrations (the HTTP surface; not always present).
  Program.cs                      # Composition root.
  ServiceName.csproj              # Single Web SDK project.
```

**Why feature folders work here:** each service has 1–6 use cases; finding "where does
PlaceOrder live?" is `Features/PlaceOrder.cs`. The Domain folder holds what's *genuinely
shared* across features (the `Order` aggregate, `IOrderRepository`); when something is used
by only one feature (a port, a command), it lives in that feature's file. NotificationService
is the canonical minimal case: zero Domain folder, two Features files, one Infrastructure
folder.

### When to use which

| Signal | Shape |
|---|---|
| ≤2 aggregates, ≤10 features, single team | VSA |
| Multiple aggregates with cross-cutting domain rules, heavy caching/gRPC, integration test suite | Clean Architecture |
| Test-substitution interfaces (`IFooRepository`, `IEventPublisher`) | Either pattern keeps them — the seam is consumer substitution, not the project boundary |
| Started as VSA but feature folders cross-reference each other 4+ ways | Promote shared concepts to `Domain/`; if `Domain/` is growing fast, that's the signal to consider Clean Architecture |

**Don't apply both patterns uniformly across a single service.** Pick one shape per service
and commit. The diff between the two patterns *across services* is intentional — it's the
project's lesson, not an inconsistency to clean up.

## Coding Standards

- .NET 10 / C# 13
- File-scoped namespaces
- Private *instance* fields prefixed with `_` (camelCase). Constants and `static readonly` fields use PascalCase per .NET convention — do NOT prefix with `_` (e.g. `OrdersPlaced`, `Carriers`, `TraceIdKey`). The `.editorconfig` enforces this split via separate naming rules
- Async methods suffixed with `Async`
- Interfaces prefixed with `I`
- Use `var` when type is apparent
- TreatWarningsAsErrors is enabled - zero warnings allowed
- Static analyzers: Meziantou, SonarAnalyzer, Roslynator

## Commenting Convention

Two tiers:

- **Architecturally significant files** (domain entities, command/event/query handlers, repositories, DbContexts, Wolverine middleware, ServiceDefaults helpers): include teaching-grade XML docs and inline comments. Explain SOLID intent (SRP, OCP, encapsulation), perf implications (`AsNoTracking`, projection, transactional outbox), idempotency mechanisms, and the *why* behind non-obvious choices. Aim for "a junior dev can read this file end-to-end and learn the pattern" — not encyclopedic, but generous with context. Do not strip these comments on subsequent edits.
- **Trivial files** (DTOs, FluentValidation validators, generated EF migrations, simple endpoint registrations, csproj, AppHost, gRPC `.proto` glue): no comments unless something is genuinely non-obvious. Names carry the meaning.

The original "default to no comments" guidance still applies when *adding new code that doesn't fit tier 1* — don't sprinkle WHAT-comments across plumbing, never leave PR-relative comments ("added for X", "fixes #123"), never comment around well-named identifiers. The tier-1 carve-out is about *teaching the architecture*, not narrating every method.

## Debugging Discipline

When a debugging session surfaces a non-obvious failure mode — framework version traps, configuration silently overriding another, an ordering gotcha, an API behavior that contradicts the docs, a backwards-incompatible change between major versions — capture the lesson **before moving on**:

1. **CLAUDE.md** gets a one-liner under the most relevant section (Package Management for version traps, Communication Patterns for messaging gotchas, Performance Rules for runtime traps, etc.) so future code generation doesn't reintroduce the same mistake.
2. **The most relevant doc** ([architecture.md](docs/architecture.md), [performance-and-data-correctness.md](docs/performance-and-data-correctness.md), etc.) gets the *why* — the rationale or convention behind the rule.
3. **[STATUS.md](docs/STATUS.md) "Open issues"** captures anything that's deferred rather than fully resolved.

The bar isn't "document every bug fix." It's: **if the failure mode would surprise the next person who hits it, the surprise belongs in writing.** Trivial typos and one-off mistakes don't qualify; framework migration gotchas, undocumented constraints, and rules-discovered-the-hard-way always do.

**When tightening or changing a CLAUDE.md rule**, grep the repo for files that paraphrase it (inline comments in `.cs`/`.props`/`.csproj`, supporting docs, README sections) and update each so they stay aligned. CLAUDE.md is canonical; everywhere else summarizes. Convention: any inline comment that summarizes a CLAUDE.md rule ends with `See CLAUDE.md.` so it's findable via `grep -rn "See CLAUDE.md"`. A PostToolUse hook surfaces candidate files automatically when CLAUDE.md is edited (see `.claude/settings.json`).

This rule is for everyone working in this repo (humans, AI assistants, future-you). Don't wait to be asked.

## Package Management

- Central Package Management via `Directory.Packages.props` - all versions defined there
- Individual `.csproj` files reference packages WITHOUT version attributes
- Shared build settings in `Directory.Build.props`
- **Aspire SDK and runtime packages must match — including minor versions.** The `Aspire.AppHost.Sdk/X.Y.Z` declared in `NextAurora.AppHost.csproj` and the `Aspire.Hosting.*` package versions in `Directory.Packages.props` need to match exactly (or the SDK ≥ packages). Major mismatches surface at *build/startup* as `TypeLoadException` (internal types like `PublishingContext`). Minor mismatches surface at *runtime* as DCP rejecting startup with `Newer version of the Aspire.Hosting.AppHost package is required`. Bump SDK and packages together as one change.
- **Service Bus subscription names are globally unique within the namespace** (Aspire 13+). Don't reuse the same subscription name on different topics — `DistributedApplicationException` at AppHost startup. Convention: `{consumer}-{source-events}-sub` (e.g. `notify-orders-sub`, `notify-payments-sub`). When adding a new subscription in `AppHost.cs`, also update the matching `ListenToAzureServiceBusSubscription("{topic}/{sub}")` string in the consuming service's `Program.cs`.
- **Aspire 13+ Azure resources need explicit local-dev fallbacks.** `AddAzureServiceBus` requires a chained `.RunAsEmulator()` for local runs (the implicit emulator behavior from Aspire 9 is gone). `AddAzureApplicationInsights` has no local emulator at all — gate it on `builder.ExecutionContext.IsPublishMode` and skip in dev. Without these, AppHost's resource pane shows "Missing subscription configuration" and every service that `WithReference`s the resource fails to start.
- **`WithReference(x)` ≠ wait-for-healthy in Aspire 13.** `WithReference` only injects connection strings / endpoints; the service starts as soon as its env vars are resolvable, even if the target is still warming up. Containers like the Service Bus emulator take 30-60s to be healthy on first run; services that race past that crash with "connection refused" and exit. **Hard rule: every `WithReference` on a non-trivial dependency (DB, messaging, identity, peer service) gets a matching `.WaitFor(x)`.** Without it, the Aspire dashboard shows services as "Finished" instead of "Running" because they exited before infra was ready.

## Communication Patterns

- **Async events** (Azure Service Bus): For workflow orchestration (order -> payment -> shipping -> notification)
- **gRPC** (sync): For real-time queries between services (OrderService -> CatalogService product validation). gRPC is versioned separately via `.proto` `package` declarations.
- **REST** (HTTP): For frontend-to-service communication only. URL-segment versioned via `Asp.Versioning.Http` — every endpoint lives under `/api/v{version}/...`. Default version is `1.0`; the version segment is required (`AssumeDefaultVersionWhenUnspecified = false`). **Always use `app.MapV1ApiGroup("Tag", "resource")`** (helper in `NextAurora.ServiceDefaults`) to register a versioned route group — it returns a `RouteGroupBuilder` rooted at `/api/v1/resource` and applies the version + tag in one call. Don't hand-roll `NewVersionedApi(...).MapGroup(...).HasApiVersion(...)` chains — drift across services is the failure mode. To add v2 later, register a side-by-side group with `.HasApiVersion(new ApiVersion(2, 0))`; v1 keeps working untouched.

## Key Conventions

- Commands return the created entity's ID (Guid)
- Queries return DTOs, never domain entities
- Domain entities use factory methods (`Create()`) with validation, not public constructors
- Event handlers must be idempotent
- Use the Outbox pattern for guaranteed event publishing (save entity + event in same transaction)
- All API error responses use RFC 7807 ProblemDetails via `GlobalExceptionHandler` (in `NextAurora.ServiceDefaults`). Never expose internal state, entity IDs, or stack traces to clients — log details server-side and return generic detail with the trace ID
- Never commit .env files, connection strings, or secrets

## Performance Rules

These are always-on. Deeper guidance (modern EF features, transactions, caching strategies, GC pressure, migrations, benchmarking) lives in the `dotnet-performance` skill.

- **EF Core reads**: always `AsNoTracking()` + projection (`.Select(...)` to a DTO). Queries return DTOs, never tracked entities. Writes load the aggregate (tracked) because they mutate it. If you must `Include` an entity graph without tracking, use `AsNoTrackingWithIdentityResolution()` (plain `AsNoTracking() + Include` duplicates shared related objects).
- **No N+1**: use `Include` or projection. Never query inside a `foreach` over results from another query.
- **Async on request paths**: `await` everywhere. Never `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`. Every async method on a request path takes and propagates `CancellationToken`.
- **Pagination**: every list endpoint must paginate with a server-side size cap (≤ 100). Use keyset pagination for large offsets.
- **Bulk ops**: use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` — never load thousands of rows just to mutate or delete them.
- **Optimistic concurrency**: every updatable aggregate must have a concurrency token (Postgres `xmin` or a row-version column). Last-write-wins is not acceptable.
- **Outbox atomicity**: the entity write and outbox-row write commit in the same transaction. Prefer one `SaveChanges` call; otherwise use `BeginTransactionAsync` explicitly.
- **`DbContext` is not thread-safe**: parallel queries (`Task.WhenAll`) require `IDbContextFactory<T>` — one context per task. The scoped per-request context handles only sequential work.
- **Structured logging**: use message templates (`"User {UserId} logged in"`) with parameter placeholders, never string concatenation or interpolation. This is also required for the correlation/user/session scope to work.
- **No logging in tight loops**: log summaries (`"Processed {Count} items"`), not per-item lines.
- **DB connection hold time**: open → query → dispose. Don't `await` unrelated work (HTTP calls, message publishes) while a connection is open.
- **Cache invalidation in the write path**: if a handler mutates a cached entity, it must invalidate or update the cache in the same handler — not "later" or "via TTL".
- **Migrations are immutable once applied**: never edit a migration that has run anywhere (dev included). Destructive changes (drop column/table, rename, NOT NULL on existing column) need a multi-step plan, not a single migration.
- **Measure before optimizing**: don't add caching, compiled queries, `ValueTask`, or `AsSplitQuery()` on intuition. Use BenchmarkDotNet for code paths, `dotnet-counters`/k6 for system behavior, `ToQueryString()` for EF.
- **Dapper is the sanctioned escape hatch from EF**, not a peer abstraction. Reach for it only when (a) the SQL is provider-specific and doesn't translate cleanly, (b) profiling proves EF is the bottleneck on a hot path, or (c) the query is a SQL aggregation where LINQ obscures intent. Always use `ctx.Database.GetDbConnection()` so Dapper shares the EF connection and any ambient transaction — never open a separate `NpgsqlConnection`/`SqlConnection`. Writes always go through aggregates + EF (Dapper bypasses concurrency tokens, domain validation, and the outbox). Full rationale: [docs/performance-and-data-correctness.md "Decision: when to reach past EF Core (Dapper escape hatch)"](docs/performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch).

## Testing

- Unit tests for domain logic and handlers
- Integration tests with Testcontainers for infrastructure — `tests/{Service}.Tests.Integration`, booting the real API via `WebApplicationFactory<Program>`. **CatalogService** slice (Postgres + Redis: caching + concurrency token) and **OrderService** slice (SQL Server + Wolverine stubbed transport: outbox, saga handlers, `RowVersion` token) exist; pattern documented in each project's README.
- **Integration tests need Docker.** On macOS, Docker Desktop's socket is at `~/.docker/run/docker.sock`, not `/var/run/docker.sock` — Testcontainers fails fast with `DockerUnavailableException` unless `DOCKER_HOST` points there (or Docker Desktop's "default Docker socket" toggle is on). CI runners have it at the standard path.
- Run `dotnet build` to verify - all analyzer warnings are errors

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project NextAurora.AppHost  # Starts everything via Aspire
```

## Observability & Context Propagation

### Correlation ID, User ID, Session ID

Every HTTP request and Service Bus message carries three context identifiers:

| Concept | Activity Baggage Key | HTTP / SB Property | Logger Scope Key |
|---------|--------------------|--------------------|-----------------|
| Correlation | `correlation.id` | `X-Correlation-Id` | `CorrelationId` |
| User | `user.id` | `X-User-Id` | `UserId` |
| Session | `session.id` | `X-Session-Id` | `SessionId` |

**Sources:**
- `correlation.id` — from `X-Correlation-Id` request header, or generated from trace ID
- `user.id` — from `ClaimTypes.NameIdentifier` JWT claim (`sub`); null when unauthenticated
- `session.id` — from `X-Session-Id` request header (client-generated browser/app session UUID); null if not provided

All three are set by `CorrelationIdMiddleware` (HTTP entry point) and by `ContextPropagationMiddleware` (Wolverine incoming-message middleware, async entry point). All three are propagated onto outgoing Wolverine messages by `OutgoingContextMiddleware`. Both middlewares are wired via the `opts.AddNextAuroraContextPropagation()` extension in each service's `Program.cs`.

### Wolverine middleware classes must use instance methods

`opts.Policies.AddMiddleware<T>()` only discovers `Before`/`After`/`Finally` (and their `Async` variants) as **instance methods** on a public class with a public constructor. Static methods aren't discovered — registration throws `InvalidWolverineMiddlewareException` at host startup. This applies even when the method has no instance state. Suppress S2325 ("should be static") with a `Justification` referencing this rule rather than satisfying the analyzer.

### Wolverine pipeline scope

`ContextPropagationMiddleware` opens a `logger.BeginScope()` before invoking each handler so **every log line emitted anywhere in the handler** carries `CorrelationId`, `UserId`, and `SessionId` automatically. Wolverine's `Policies.LogMessageStarting()` adds handler name + elapsed time on top of that.

Order in the Wolverine pipeline: FluentValidation policy (`opts.UseFluentValidation()`, rejects invalid commands before handlers run) → `ContextPropagationMiddleware` (opens logger scope) → handler. `opts.Policies.AutoApplyTransactions()` wraps each EF-touching handler chain so outgoing messages are persisted to the outbox in the same DB transaction as the entity write.

### Wolverine envelope context extraction

Handlers don't extract context manually — `ContextPropagationMiddleware` does it for them. The middleware reads `Envelope.Headers["X-Correlation-Id" | "X-User-Id" | "X-Session-Id"]` (Wolverine's transport-agnostic header bag, mapped to Service Bus `ApplicationProperties` over the wire), restores them into Activity baggage, and opens a `logger.BeginScope()`. After the handler runs, `Finally()` disposes the scope.

Outgoing context is stamped by `OutgoingContextMiddleware`, which reads Activity baggage and writes the same headers onto outgoing envelopes. The full mechanism is registered via `opts.AddNextAuroraContextPropagation()` in each service's `Program.cs`.

Never add null/empty keys to logging scope dictionaries — use `if (x is not null) scope["Key"] = x`. Always pass `StringComparer.Ordinal` when constructing `Dictionary<string, T>` (per Meziantou MA0002).

### Transactional Outbox (Wolverine)

Each event-publishing service (Order, Payment, Shipping) runs Wolverine's transactional outbox. Outgoing events are persisted to a `wolverine.*` schema in the same DB transaction as the entity write, then dispatched to Azure Service Bus by Wolverine's background flush. Configuration lives in each service's `Program.cs`:

```csharp
opts.PersistMessagesWithSqlServer(connectionString, "wolverine");   // or PersistMessagesWithPostgresql
opts.UseEntityFrameworkCoreTransactions();
opts.Policies.AutoApplyTransactions();
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
```

`builder.Services.AddResourceSetupOnStartup()` auto-creates outbox tables on app startup. This means the entity write and the event publish either both commit or neither does — no more lost events on bus failure or process crash. See [docs/performance-and-data-correctness.md](docs/performance-and-data-correctness.md) for the full rationale and failure modes addressed.

### Event Replay

Replay is handled through Wolverine's own message-store and DLQ tooling. The previous hand-rolled `EventLogs` table and `/admin/events` endpoints were deleted as dead code post-Wolverine — they were only ever populated by replay records of replays. If operator-facing event browsing is needed, build it on top of `IMessageStore` (Wolverine's API) or the `wolverine.outgoing_envelopes` table directly.
