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
- **Interfaces earn their keep through *consumer substitution*, not "future swap"**: a port/adapter interface (`IFooGateway`, `IEventPublisher`, `IFooSender`, `IFooResolver`) is justified when at least one of: **(a)** it's substituted by tests today (NSubstitute mock, fake, in-memory double — verify with `grep "Substitute.For<IFoo"`), **(b)** there are two or more concrete implementations registered against it today (dev + prod adapter, multi-tenant variants), or **(c)** a second implementation is on a *concrete* near-term roadmap item — not "we might want X someday." If none of (a)/(b)/(c) holds, the interface is speculative coupling and should be deleted; the handler can take the concrete class directly.
- **Repository interfaces are NOT justified by this rule** (see "Data access: DbContext directly, no repository wrappers" below). `DbContext`/`DbSet<T>` already IS the Repository + Unit-of-Work pattern; wrapping it in `IFooRepository` adds layers without adding capability. The test-substitutability defense (mocking `IOrderRepository` in unit tests) fails because the right tests for EF-touching handlers are integration tests with Testcontainers, not unit tests with mocks (see "Testing" rule). Justified ports today: `IEventPublisher` (Wolverine vs. test fake), `IPaymentGateway` (Stripe vs. test fake), `ICatalogClient` (gRPC vs. test fake), `INotificationSender` (console vs. SendGrid/Twilio), `IProductCache` (HybridCache vs. test fake), `IProductReadStore` (CatalogService Clean Architecture variant — Domain can't reference Contracts where DTOs live, so the read interface lives in Application; the alternative is to push DTOs into Domain, which would violate the layer rule). Past deletions: `IRecipientResolver`/`StubRecipientResolver` (no test substitution, no second impl), the four entity-returning repositories (`IOrderRepository`, `IPaymentRepository`, `IShipmentRepository`, `IProductRepository` — handlers now take `DbContext` directly; tests moved to integration).

### Data access: DbContext directly, no repository wrappers

- **Handlers take `DbContext` (or `IDbContextFactory<T>`) directly. No `IFooRepository` interfaces.** `Microsoft.EntityFrameworkCore.DbContext` is already the Unit of Work; `DbSet<T>` is already the Repository. A wrapper interface (`IOrderRepository`) adds a layer without adding capability — and the only reason to add the layer was to enable mocking in unit tests, which we've replaced with integration tests against real Testcontainers DBs.
- **Reads project to DTOs inside the IQueryable.** `context.Orders.AsNoTracking().Where(...).Select(o => new OrderSummaryDto { ... }).ToListAsync(ct)` — directly in the handler, no method wrapping, no in-memory mapper. The projection IS the read contract. EF auto-splits projected collection navigations, so no parent-cartesian rows on the wire (see [docs/cqrs-data-access.md](docs/cqrs-data-access.md) for the mechanism).
- **Writes load the aggregate tracked and call `SaveChangesAsync`.** `var order = await context.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct); ...; order.MarkAsPaid(); await context.SaveChangesAsync(ct);` Optimistic concurrency tokens fire on `SaveChanges`; `AddConcurrencyRetry` handles `DbUpdateConcurrencyException` for handler-pipeline code.
- **Exception — CatalogService (Clean Architecture variant) keeps both `IProductRepository` (write) and `IProductReadStore` (read).** Application can't reference Infrastructure without creating a circular project reference (`Infrastructure → Application` already exists for `IProductCache`/`IProductReadStore`), and `CatalogDbContext` lives in Infrastructure. So Application handlers cannot take `CatalogDbContext` directly — they need the abstraction. This is a real layer-rule consequence, not "wrapping for the sake of wrapping": both repositories pass the substitution test through Clean Architecture's project-reference constraints. The VSA services have no such constraint (handlers and DbContext live in the same csproj), so they drop the wrapper. The diff between the two patterns — wrapper kept where the layer rule needs it, wrapper dropped where it doesn't — is the project's intentional calibration story. See `CatalogService.Application/Interfaces/IProductReadStore.cs` and `CatalogService.Domain/Interfaces/IProductRepository.cs`.
- **Exception: outbox-atomic non-handler code.** `BackgroundService` sweepers and other code outside the Wolverine handler pipeline still need an explicit transactional wrap (`BeginTransactionAsync` → work → `SaveChangesAsync` → `CommitAsync`) so Wolverine's staged outbox envelopes persist atomically with entity writes. This used to live in `IPaymentRepository.ExecuteInTransactionAsync`; it now lives inline in the recovery job. Pattern is unchanged; the wrapper interface is gone.

### Domain-Driven Design

- **Rich Domain Entities** (when warranted): Entities that are *persisted* and have *non-trivial, observable invariants* must enforce them — state changes go through methods, never public setters, with factory methods (static `Create()`) that validate inputs. **The pattern only earns its keep when someone observes the invariant.** If the entity is in-memory, single-use, and discarded after the handler returns, skip the aggregate shape entirely — inline the validation, or use a FluentValidation rule on the command. A factory + private setters + status enum that nothing reads is ceremony, not architecture. NotificationService is the canonical "no aggregate" example: stateless event-to-email pump, no persistence, no domain rules worth protecting.
- **Value Objects**: Use value objects for concepts like Money (amount + currency), Quantity (non-negative int). They enforce rules at construction.
- **Aggregates**: Each aggregate root controls access to its children. Do not expose mutable collections. Add methods like `AddLine()` instead of exposing `List<T>`.
- **Domain Events**: State changes that affect other bounded contexts should raise domain events.
- **Layer Dependencies**: Domain -> nothing. Application -> Domain. Infrastructure -> Domain + Application. Api -> all layers (composition root). **A service with no domain entities doesn't need a Domain project** — ports (`I*Sender`, `I*Resolver`) live in `Application/Interfaces/` instead. NotificationService is the precedent: 3 projects (Api/Application/Infrastructure), no Domain.

### Security Requirements

- **Authentication**: All non-public endpoints must use `.RequireAuthorization()`. JWT Bearer authentication.
- **Authorization (the concrete pattern, not just the principle)**: Users can only access their own resources. The canonical shape for buyer-scoped reads — applied because a missing scope check is an IDOR (CWE-639), and IDORs slip through tests-by-omission:
  - Endpoint reads `ClaimTypes.NameIdentifier` from the JWT → passes as `RequestingBuyerId` into the query/command.
  - Handler loads the entity, then **returns null** on owner mismatch (NOT throws, NOT returns 403).
  - Endpoint translates `null` to **404**. Returning **403 is wrong** here — it leaks existence ("this resource exists, just not yours"). 404 is indistinguishable from "not found."
  - Reference templates: `OrderEndpoints.cs:GET /orders/{id}` (commit-on-record after fix), `ShippingEndpoints.cs:GET /shipments/order/{orderId}`, `CatalogEndpoints.cs:PUT /products/{id}` (seller-scope variant — defense in depth at endpoint AND handler).
  - **An integration test asserting buyer X cannot read buyer Y's entity is required** when adding any scoped-entity endpoint. The absence of such a test is how IDORs survive — see CLAUDE.md "Testing" rule.
- **JWT validation (explicit, not implicit)**: `TokenValidationParameters` must explicitly set:
  - `ValidateIssuerSigningKey = true` (default validates via JWKS, but explicit is auditable).
  - `ClockSkew = TimeSpan.FromSeconds(30)` — default is **5 minutes**, which means revoked/expired tokens stay accepted for 5 extra minutes. Material on typical 15-minute access-token lifetimes.
  - `ValidateAudience`, `ValidateIssuer`, `ValidateLifetime` all `true`. See [Extensions.cs `AddDefaultAuthentication`](NextAurora.ServiceDefaults/Extensions.cs).
- **Input Validation**: All commands must have FluentValidation validators. Validate at the API boundary before reaching handlers.
- **Error Handling**: Never expose internal state, stack traces, or entity IDs in API responses. Log details server-side, return generic errors with correlation IDs to clients.
  - **Response `traceId` field uses `Activity.TraceId.ToString()` only** (32 hex chars), NOT `Activity.Id` (the full W3C traceparent `00-<trace>-<span>-<flags>` — span ID leaks server-side handler call structure to clients). See [GlobalExceptionHandler.cs](NextAurora.ServiceDefaults/GlobalExceptionHandler.cs).
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

## Continuous Rule Encoding (the compounding loop)

The Debugging Discipline rule above covers failures discovered while *debugging*. The same discipline applies to patterns + antipatterns discovered via *any* review surface — architecture-reviewer agent passes, CodeRabbit findings, manual code review, integration-test failures, prod incidents, security audits. **Anything that earns the label "we should never write this again" or "we should always do this when" belongs encoded in `.claude/` config + supporting docs, the same session it's identified.** Otherwise the same finding resurfaces in a future review and the cycle wastes attention.

When you find an antipattern, rule, or specification worth encoding, write to ALL of these that apply:

1. **CLAUDE.md** — the canonical hard/soft rule. The most relevant existing section. New section only if no fit.
2. **`.coderabbit.yaml`** `path_instructions` — file-pattern-scoped guidance so CodeRabbit catches future violations at PR-review time without re-deriving the rule. Use the existing `path:` glob entries; add a new one if no fit.
3. **[`.claude/agents/architecture-reviewer.md`](.claude/agents/architecture-reviewer.md)** "Pattern checklist" — a scan rule the agent applies on every review touching the relevant file category. So the next architectural pass catches it before code lands.
4. **[`.claude/skills/`](.claude/skills/)** — if the pattern is non-trivial enough to warrant a procedure (multi-step reasoning, specialized vocabulary), it becomes a skill. Otherwise the path_instructions + CLAUDE.md rule is enough.
5. **[`docs/STATUS.md`](docs/STATUS.md) "Open issues"** — if the finding is deferred or partial.
6. **Supporting docs** ([`docs/architecture.md`](docs/architecture.md), [`docs/performance-and-data-correctness.md`](docs/performance-and-data-correctness.md), [`docs/dev-loop.md`](docs/dev-loop.md)) — when the *why* deserves more than a CLAUDE.md one-liner.

The threshold for encoding is the same as the Debugging Discipline rule: **if the next person could repeat the mistake (or re-derive the rule from first principles), the rule belongs in writing.** Don't encode trivial style nits or one-off mistakes; do encode security patterns, performance traps, concurrency hazards, distributed-systems gotchas, anti-IDOR patterns, anti-IDOR-test patterns, outbox traps, anything cross-cutting.

When you push a fix PR for a real finding, the *fix itself* lives in the PR but the *rule* lives in `.claude/`. The two should land together when feasible (single PR with both), or as paired PRs when separation is cleaner. **A merged fix PR without the corresponding rule is a half-finished job** — the next instance of the same antipattern will slip through.

This rule is for humans, AI assistants, and future-you. Don't wait to be asked.

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
- **Wolverine handler discovery is NOT DI registration — two separate containers.** `opts.Discovery.IncludeAssembly(...)` builds Wolverine's *internal* handler-type map (message-type → handler-type) used by `IMessageBus.InvokeAsync<T>()` / `PublishAsync<T>()`. Wolverine constructs handlers itself via `IServiceScopeFactory` — it never asks `IServiceCollection` for the handler type. Therefore: **`serviceProvider.GetRequiredService<MyHandler>()` throws `InvalidOperationException` unless you also `AddScoped<MyHandler>()`.** Production code paths go through `IMessageBus` and never hit this; the path that does hit it is **integration tests that resolve handlers directly to assert the EF projection SQL** (read-handler integration tests, by far the most common case). Rule: any handler resolved by `GetRequiredService<T>()` in tests must have an explicit `services.AddScoped<T>()` in that service's `AddXInfrastructure` registration. Reference: [OrderService/Infrastructure/DependencyInjection.cs](OrderService/Infrastructure/DependencyInjection.cs) (the `AddScoped<GetOrderByIdHandler>()` / `AddScoped<GetOrdersByBuyerHandler>()` pair). Failure mode that surfaced this gap: `OrderReadProjectionTests` failed in CI with `No service for type 'OrderService.Features.GetOrderByIdHandler' has been registered` after the repository-wrapper refactor — pre-refactor the tests resolved `IOrderRepository` (registered as `AddScoped<IOrderRepository, OrderRepository>()`), and the conversion to handler-resolved tests missed the equivalent registration.

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

- **EF Core reads — project in EF, not in memory.** Read paths must `AsNoTracking()` + `.Select(...)` into a DTO **inside the IQueryable**, and the repo/query method **returns the DTO**, not a domain entity. Two distinct wins: (1) SQL emits only the DTO's columns instead of every column on the entity, (2) when the DTO includes a nested collection (`Lines = o.Lines.Select(...).ToList()`), EF Core auto-splits into a separate query for the children — so the parent isn't repeated across a JOIN and there are no cartesian rows over the wire. Mapping in the handler (`repo.GetX() → entity → Mapper.ToDto(entity)`) is the anti-pattern: it forces EF into a single-JOIN query (the entity graph must materialize as the JOIN says), you pay for the wasted columns *and* the cartesian rows from the JOIN, and you double-materialize (rows → entity → DTO). If you genuinely must materialize an entity graph without tracking — rare on a read path — use `AsNoTrackingWithIdentityResolution()` so duplicate **client-side objects** stitch into one parent; note the cartesian SQL rows still hit the wire (for that, `AsSplitQuery()`). Writes load the aggregate tracked because they mutate it. **Read/write split is the rule, not "future cleanup":** when the same `GetByIdAsync` is shared between a query handler and a command/event/saga handler, add a sibling read method that projects to DTO; the entity-returning method stays for the write path. See [docs/cqrs-data-access.md](docs/cqrs-data-access.md) for the canonical shape per architecture style (VSA: sibling DTO method on the existing repo interface; Clean: separate `IFooReadStore` in Application) and "Why projection kills cartesian rows" for the EF mechanism.
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
- **Test structure — AAA with narrative comments.** Every test is structured as **Arrange → Act → Assert** with `// ARRANGE`, `// ACT`, `// ASSERT` markers (all caps, em-dash explanation on the same line). Each phase carries a *story comment*: explain what's being set up and *why it matters*, what's being called, and what each assertion is verifying. A junior dev should be able to read a single test top-to-bottom and understand the contract being checked + the failure mode being guarded against — without having to read the SUT first. When the ASSERT phase verifies multiple invariants, number them and explain why each matters (especially for security boundaries, idempotency guards, and ordering-sensitive operations like cache-after-save). Trivial happy-path tests can be shorter; security/concurrency/idempotency tests get the full story. Reference templates: [UpdateProductHandlerTests.cs](tests/CatalogService.Tests.Unit/Application/UpdateProductHandlerTests.cs) (security + cache ordering), [PaymentFailedHandlerTests.cs](tests/OrderService.Tests.Unit/Application/PaymentFailedHandlerTests.cs) (idempotency under at-least-once delivery), [GetShipmentByOrderHandlerTests.cs](tests/ShippingService.Tests.Unit/Application/GetShipmentByOrderHandlerTests.cs) (IDOR-prevention pattern).
- Integration tests with Testcontainers for infrastructure — `tests/{Service}.Tests.Integration`, booting the real API via `WebApplicationFactory<Program>`. **CatalogService** slice (Postgres + Redis: caching + concurrency token) and **OrderService** slice (SQL Server + Wolverine stubbed transport: outbox, saga handlers, `RowVersion` token) exist; pattern documented in each project's README.
- **Integration tests need Docker.** On macOS, Docker Desktop's socket is at `~/.docker/run/docker.sock`, not `/var/run/docker.sock` — Testcontainers fails fast with `DockerUnavailableException` unless `DOCKER_HOST` points there (or Docker Desktop's "default Docker socket" toggle is on). CI runners have it at the standard path.
- **IDOR test is required for every new endpoint that returns or mutates a scoped entity.** Add an integration test that authenticates as buyer X, requests a resource owned by buyer Y, and asserts the response is 404 (NOT 200, NOT 403 — see Security Requirements). The absence of such a test is exactly how the original `GET /api/v1/orders/{id}` IDOR survived undetected for the lifetime of the codebase. A `dotnet build` clean and unit tests passing aren't sufficient — *authorization behavior is only proven by an authorization-failure test*.
- **Outbox-in-non-handler test.** Code paths that publish events from outside a Wolverine handler (BackgroundService sweepers, recovery jobs) need an integration test that asserts a row appears in `wolverine.outgoing_envelopes` in the same transaction as the entity write. See the outbox-outside-handler trap in Observability → Transactional Outbox. The PaymentRecoveryJob outbox bug survived because no test asserted that the staged envelope actually persisted.
- **Handlers resolved directly in tests must be DI-registered.** If an integration test does `scope.ServiceProvider.GetRequiredService<MyHandler>()`, the handler must be `AddScoped<MyHandler>()`'d in `AddXInfrastructure`. Wolverine's handler-discovery does not populate `IServiceCollection` — it builds its own internal map. See "Communication Patterns → Wolverine handler discovery is NOT DI registration" for the full mechanism. Failure mode: `InvalidOperationException: No service for type 'X.Handler' has been registered` at first test run. Catch this at PR review time, not in CI — `/check-rules` audit pattern for the test diff: every `GetRequiredService<*Handler>()` line needs a matching `AddScoped<*Handler>()` in DI.
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

**HTTP middleware order — strict.** `CorrelationIdMiddleware` reads `ClaimTypes.NameIdentifier` from `context.User` to populate the `UserId` scope. That requires running AFTER `UseAuthentication` (otherwise `context.User` is empty and `UserId` is silently always null — defeats the audit pipeline). It also must run BEFORE `UseAuthorization` so the `UserId` scope is active during the authorization decision — any 401/403 denial gets logged with the authenticated user's ID, preserving the audit trail for "user X tried to access resource they shouldn't." Canonical order in `MapDefaultEndpoints`:

```csharp
app.UseExceptionHandler();                          // wraps every error below
app.UseAuthentication();                            // populates context.User
app.UseMiddleware<CorrelationIdMiddleware>();       // reads User, opens log scope
app.UseAuthorization();                             // 401/403 attributed to UserId
```

Reference: [Extensions.cs `MapDefaultEndpoints`](NextAurora.ServiceDefaults/Extensions.cs).

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

**Outbox outside a Wolverine handler — atomicity trap.** `AutoApplyTransactions` only wraps Wolverine handler chains. Code that runs OUTSIDE a handler (`BackgroundService` sweepers, cron-style recovery jobs, admin endpoints, anything publishing events from a non-handler context) does NOT get the outbox-atomic transaction wrap for free. The trap: `IMessageBus.PublishAsync(...)` stages an envelope into the `wolverine.outgoing_envelopes` tracker, but **the envelope is only persisted when `SaveChangesAsync` runs after the publish call**. Wolverine's `UseEntityFrameworkCoreTransactions` intercepts `SaveChanges` to bridge the staged envelope into the DB transaction. If your wrapper does `BeginTransactionAsync` → entity write + publish → `Commit` *without an explicit `SaveChangesAsync` between the publish and the commit*, the envelope stays in the tracker, the transaction commits without it, and the event is silently dropped.

The canonical safe wrapper:
```csharp
public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
{
    await using var tx = await context.Database.BeginTransactionAsync(ct);
    await work(ct);                          // entity write + PublishAsync inside here
    await context.SaveChangesAsync(ct);      // flushes Wolverine's staged envelope
    await tx.CommitAsync(ct);
}
```
Reference: [PaymentRepository.ExecuteInTransactionAsync](PaymentService/Infrastructure/PaymentRepository.cs) (fixed in the commit captured by docs/STATUS.md). When adding a non-handler code path that publishes events, **either** wrap it in this pattern **or** factor the publish back into a Wolverine handler triggered by an internal scheduled message.

### Event Replay

Replay is handled through Wolverine's own message-store and DLQ tooling. The previous hand-rolled `EventLogs` table and `/admin/events` endpoints were deleted as dead code post-Wolverine — they were only ever populated by replay records of replays. If operator-facing event browsing is needed, build it on top of `IMessageStore` (Wolverine's API) or the `wolverine.outgoing_envelopes` table directly.
