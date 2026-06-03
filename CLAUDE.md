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
- **Factory pattern / `[FromKeyedServices]` is the canonical shape *once condition (b) holds* — not before.** When a port has exactly one impl, the consuming handler should just take the interface and let DI resolve the single registration. **Once a second impl actually ships** (e.g. `SendGridNotificationSender` lands alongside the existing `ConsoleNotificationSender`, or a Twilio adapter joins SMTP) **and per-call selection becomes a real decision** (different requests route to different channels), introduce the factory shape: `services.AddKeyedScoped<INotificationSender, ConsoleNotificationSender>("console")` + `services.AddKeyedScoped<INotificationSender, SendGridNotificationSender>("email")`, and resolve per-call with `[FromKeyedServices(channel)] INotificationSender sender` in the handler constructor or via `IServiceProvider.GetRequiredKeyedService<INotificationSender>(channel)`. **Do NOT pre-build the factory while there's only one impl.** A `INotificationSenderFactory` that returns the same `ConsoleNotificationSender` for every input is the same kind of speculative coupling as the deleted `I*Repository` wrappers — it adds a layer that buys nothing today and isn't shaped for whatever the second impl actually needs. NotificationService is the canonical "ready for the factory, not yet wearing it" example: `INotificationSender` exists (justified by condition (c)), `SendNotificationRequest.Channel` is already on the command record (the routing key when the time comes), but `ConsoleNotificationSender` is the only registration in DI. When SendGrid/Twilio/SES actually ship, that's the day the factory earns its keep.
- **Repository interfaces are NOT justified by this rule** (see "Data access: DbContext directly, no repository wrappers" below). `DbContext`/`DbSet<T>` already IS the Repository + Unit-of-Work pattern; wrapping it in `IFooRepository` adds layers without adding capability. The test-substitutability defense (mocking `IOrderRepository` in unit tests) fails because the right tests for EF-touching handlers are integration tests with Testcontainers, not unit tests with mocks (see "Testing" rule). Justified ports today: `IEventPublisher` (Wolverine vs. test fake), `IPaymentGateway` (Stripe vs. test fake), `ICatalogClient` (gRPC vs. test fake), `INotificationSender` (console vs. SendGrid/Twilio), `IProductCache` (HybridCache vs. test fake). Past deletions: `IRecipientResolver`/`StubRecipientResolver` (no test substitution, no second impl), the five entity-returning repositories (`IOrderRepository`, `IPaymentRepository`, `IShipmentRepository`, `IProductRepository`, plus the read-side `IProductReadStore` — handlers now take `DbContext` directly; tests moved to integration).

### Data access: DbContext directly, no repository wrappers

- **Handlers take `DbContext` (or `IDbContextFactory<T>`) directly. No `IFooRepository` interfaces.** `Microsoft.EntityFrameworkCore.DbContext` is already the Unit of Work; `DbSet<T>` is already the Repository. A wrapper interface (`IOrderRepository`) adds a layer without adding capability — and the only reason to add the layer was to enable mocking in unit tests, which we've replaced with integration tests against real Testcontainers DBs.
- **Reads project to DTOs inside the IQueryable.** `context.Orders.AsNoTracking().Where(...).Select(o => new OrderSummaryDto { ... }).ToListAsync(ct)` — directly in the handler, no method wrapping, no in-memory mapper. The projection IS the read contract. EF auto-splits projected collection navigations, so no parent-cartesian rows on the wire (see [docs/cqrs-data-access.md](docs/cqrs-data-access.md) for the mechanism).
- **Writes load the aggregate tracked and call `SaveChangesAsync`.** `var order = await context.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct); ...; order.MarkAsPaid(); await context.SaveChangesAsync(ct);` Optimistic concurrency tokens fire on `SaveChanges`; `AddConcurrencyRetry` handles `DbUpdateConcurrencyException` for handler-pipeline code.
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
  - **Read handlers push the ownership predicate INTO the EF `Where` clause** (`...Where(o => o.Id == OrderId && o.BuyerId == RequestingBuyerId)`). Non-owner rows never leave the database — single `FirstOrDefaultAsync` returns `null` if either predicate fails. Tighter than a post-materialization check because there's no in-memory comparison step that a buggy refactor could weaken.
  - **Write handlers** load the aggregate tracked (they need it to mutate) and then check the ownership field on the loaded entity, **returning `false`/`null` on mismatch** (NOT throws, NOT returns 403). Same contract; the difference is the mechanism (in-memory check is forced by the tracked-load requirement).
  - Endpoint translates `null` (or `false`) to **404**. Returning **403 is wrong** here — it leaks existence ("this resource exists, just not yours"). 404 is indistinguishable from "not found."
  - Reference templates: `OrderEndpoints.cs:GET /orders/{id}` + `Features/GetOrderById.cs` (read — predicate in SQL), `ShippingEndpoints.cs:GET /shipments/order/{orderId}` + `Features/GetShipmentByOrder.cs` (read — predicate in SQL), `CatalogEndpoints.cs:PUT /products/{id}` + `Features/UpdateProduct.cs` (seller-scope WRITE variant — defense in depth at endpoint AND handler, with in-memory check after the tracked load).
  - **An integration test asserting buyer X cannot read buyer Y's entity is required** when adding any scoped-entity endpoint. The absence of such a test is how IDORs survive — see CLAUDE.md "Testing" rule.
- **JWT validation (explicit, not implicit)**: `TokenValidationParameters` must explicitly set:
  - `ValidateIssuerSigningKey = true` (default validates via JWKS, but explicit is auditable).
  - `ClockSkew = TimeSpan.FromSeconds(30)` — default is **5 minutes**, which means revoked/expired tokens stay accepted for 5 extra minutes. Material on typical 15-minute access-token lifetimes.
  - `ValidateAudience`, `ValidateIssuer`, `ValidateLifetime` all `true`. See [Extensions.cs `AddDefaultAuthentication`](NextAurora.ServiceDefaults/Extensions.cs).
- **Input Validation**: All commands must have FluentValidation validators. Validate at the API boundary before reaching handlers.
- **Server-controlled fields are computed server-side, never trusted from the client.** Money (price, currency, tax), authorization identifiers (`BuyerId`/`SellerId` — must match JWT `sub`), state-machine columns (`Status`), and security flags (`IsAdmin`, `IsDeleted`) are server-controlled. A `[FromBody]` DTO with a `Price` field is a price-tampering vulnerability — a client can submit `Price = 0.01` for a $999 product and the server happily accepts it. **Canonical pattern:** the handler fetches the authoritative value from its source (CatalogService gRPC for product `Price` + `Currency`, the JWT `sub` claim for buyer identity, the DB for entity `Status`) and uses *that* in any money calculation, authorization check, or state transition — the request DTO is treated as untrusted input. Reference: [OrderService/Features/PlaceOrder.cs:138](OrderService/Features/PlaceOrder.cs) — `OrderLine` entities use `product.Price` from the catalog gRPC response, never a `Price` field on the request body. Related: the "Mass assignment" check in `.claude/agents/architecture-reviewer.md` flags `[FromBody]` types that bind these fields without stripping or re-validating against the authoritative source.
- **Error Handling**: Never expose internal state, stack traces, or entity IDs in API responses. Log details server-side, return generic errors with correlation IDs to clients.
  - **Response `traceId` field uses `Activity.TraceId.ToString()` only** (32 hex chars), NOT `Activity.Id` (the full W3C traceparent `00-<trace>-<span>-<flags>` — span ID leaks server-side handler call structure to clients). See [GlobalExceptionHandler.cs](NextAurora.ServiceDefaults/GlobalExceptionHandler.cs).
- **HTTPS**: Enforce HTTPS redirection in production.
- **CORS**: Explicit CORS policy allowing only known frontend origins.
- **Rate Limiting**: Applied to search and payment endpoints at minimum. **In-memory limiters silently weaken at N× the limit when scaled to N instances.** ASP.NET Core's built-in `RequireRateLimiting` + `AddFixedWindowLimiter` / `AddSlidingWindowLimiter` stores counters in-process — each instance enforces its own; a client hitting any instance gets a fresh allowance. Once a service runs 2+ instances (multiple Fly Machines, Kubernetes replicas, etc.), swap affected endpoints to a Redis-backed limiter using the project's existing Redis (already present for HybridCache in CatalogService). **Critical implementation detail:** the increment + TTL pair (`INCR` then `EXPIRE`) is two separate Redis operations and has a race window under high concurrency — use a Lua script (`EVAL`) to make the pair atomic. NextAurora is single-instance everywhere today (Catalog deployed; Order/Payment/Shipping/Notification local), so the in-memory limiter is correct *for now*. Currently rate-limited endpoints: `GET /api/v1/products/search` (CatalogService) and `POST /api/v1/payments/process` (PaymentService) — both call `AddFixedWindowLimiter` in their respective `Program.cs`. [docs/full-saga-deployment-plan.md](docs/full-saga-deployment-plan.md) Phase 3 deliverable audits this for scale-out.

## Project Structure

**Vertical Slice Architecture for every service.** All five services (Catalog, Order, Payment,
Shipping, Notification) follow the same single-project shape, organized by *feature* instead
of *kind*. The repo previously used Clean Architecture for CatalogService and VSA for the
other four; that diff was retired in the simplicity refactor because the layer split wasn't
earning its keep at this scale (~2k LOC, 2 aggregates) and the two patterns coexisting in
one repo was inconsistency without payoff.

```
ServiceName/
  Features/                       # One file per use case: command/query record + validator + handler co-located.
                                  # Saga event-handler classes also live here (they're features too).
  Domain/                         # Shared aggregates, value objects, enums, ports (interfaces consumed by features).
  Infrastructure/                 # EF Core (with /Data/ + /Migrations/), caching, gateways, DI composition.
  Endpoints/                      # Minimal-API endpoint registrations (the HTTP surface; not always present).
  Grpc/                           # gRPC server-side handlers (CatalogService only — gRPC server peer for the catalog client).
  Program.cs                      # Composition root.
  ServiceName.csproj              # Single Web SDK project.
```

**Why feature folders work here:** each service has 1–8 use cases; finding "where does
PlaceOrder live?" is `Features/PlaceOrder.cs`. The Domain folder holds what's *genuinely
shared* across features — aggregates (e.g. `Order`, `OrderLine`, `Product`), value objects,
and consumer-substitution ports (`IEventPublisher`, `ICatalogClient`, `IProductCache`).
When something is used by only one feature (a single command, query, validator), it lives
in that feature's file. NotificationService is the canonical minimal case: zero Domain
folder, two Features files, one Infrastructure folder. CatalogService is the most filled-out:
6 features, 2 aggregates, EF + HybridCache + gRPC server.

### Promotion signal — when to consider Clean Architecture

VSA is the default and stays the default. If a single service grows to 5+ aggregates with
cross-cutting domain rules that several features need to coordinate on, AND `Domain/` is
growing faster than `Features/`, that's the *earliest* signal to consider promoting to a
multi-project layout. None of the services are at that scale today and probably won't be —
the previous attempt at Clean Architecture in CatalogService was retired specifically
because we hit none of those signals.

**What "promote to Clean" actually means — the dependency rule vs. the project split.** Two
different things wear the name "Clean Architecture," and only one of them is what you'd
promote *to*:

- **The dependency rule** (Domain → nothing; infrastructure/IO at the edges) is *already in
  force* in VSA and applies at every scale. It is **not earned, not complexity-gated, and
  not what the promotion signal is about** — NextAurora keeps it today as single-project VSA
  (see "Layer Dependencies"). Decoupling the domain from frameworks is always worth it.
- **The multi-project structure** (separate Domain/Application/Infrastructure/Api csprojs) is
  the only part that carries ceremony, and the only thing the promotion signal gates. All it
  buys over single-project VSA is **compile-time enforcement** of the dependency rule via
  project references.

So the real axis is *how you enforce the dependency rule*, escalating only as the cost of a
violated boundary rises: **convention → architecture tests → project split.** The middle rung
is the one most teams skip: an **architecture test** (NetArchTest / ArchUnitNET, or a Roslyn
analyzer) asserting "Domain references no EF/Infrastructure namespaces" enforces the *same*
boundary the 4-project split does — deterministically, in CI, **without the project
ceremony**. The project split is therefore rarely needed *for enforcement alone*; reach for it
only when genuine domain complexity makes you want the *compiler* (not a test) to hold the
line, or when separate deploy/versioning units justify it. NextAurora enforces the dependency
rule today via *convention + the architecture-reviewer agent + CodeRabbit*; adding
architecture tests would be the next rung and would make it deterministic **without changing
the VSA shape**. Full portable decision guide (the two meanings of "Clean," the
convention→arch-tests→project-split spectrum, the Testcontainers testing shift, the
duplication tradeoff, when-to-use): [docs/vsa-vs-clean-architecture.md](docs/vsa-vs-clean-architecture.md).

| Signal | Shape |
|---|---|
| ≤4 aggregates per service, ≤10 features, single team | VSA (current default) |
| "I want the dependency rule enforced, not just held by convention" | **Architecture tests** (NetArchTest / analyzer) — NOT a project split. Same boundary, no ceremony |
| 5+ aggregates with cross-cutting domain rules that several features coordinate on, AND `Domain/` growing faster than `Features/` | Consider the multi-project split — when you want the *compiler* to hold the boundary, or need separate deploy/versioning units |
| "I want to mock the DbContext in unit tests" | NOT a reason. Use integration tests with Testcontainers; see "Testing" rule |

**Don't apply both patterns uniformly across a single service.** Pick one shape per service
and commit. The diff between the two patterns *across services* is intentional — it's the
project's lesson, not an inconsistency to clean up.

## Coding Standards

- .NET 10 / C# 13
- File-scoped namespaces
- Private *instance* fields prefixed with `_` (camelCase). Constants and `static readonly` fields use PascalCase per .NET convention — do NOT prefix with `_` (e.g. `OrdersPlaced`, `Carriers`, `TraceIdKey`). The `.editorconfig` enforces this split via separate naming rules
- Async methods suffixed with `Async`
- Interfaces prefixed with `I`
- **Model names are intent-based, by role — prefer a specific suffix over a generic `Dto` on a request model.** A generic suffix like `CreateOrderDto` only says "data transfer object"; it doesn't say what the type *does* or where it belongs. Name by role instead:
  - **CQRS messages:** `*Command` for writes (`PlaceOrderCommand`, `ProcessPaymentCommand`), `*Query` for reads (`GetOrderByIdQuery`, `SearchProductsQuery`). These are *more* intent-revealing than a generic `*Request` — they say write-vs-read *and* that the type flows the Wolverine message pipeline. This is the canonical inbound shape; do NOT introduce a `*Dto`/`*Request` request model where a Command/Query fits.
  - **gRPC contracts:** `*Request` / `*Response` pairs (`GetProductRequest` / `ProductResponse`) — the gRPC idiom, already used in CatalogService.
  - **Read projections:** `*Dto` (`OrderSummaryDto`, `ProductDto`) — the query-result shape a query handler projects to. `Dto` is acceptable *here only*, where it correctly signals "read-side projection / transfer shape" in a CQRS context; it is NOT coupled to HTTP, so don't rename it to `*Response`.
  - The rule in one line: **generic `*Dto` on a request model hides intent — name it by what it does; reserve `*Dto` for read projections.**
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
5. **GitHub Issues** — if the finding is deferred or partial. Open an issue with `rule-encoding-deferred` (when code shipped but the encoding is still pending) or the relevant `type/*` + `area/*` labels. The issue is the durable record; STATUS.md no longer carries an "Open issues" list (it's now a thin entry-point doc pointing at the issues board).
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
- **Durability ≠ replay — don't reach for a stream just to avoid losing messages.** A common misconception is "pub/sub loses the message if a subscriber is down, so use a stream (Kafka/Event Hubs) when you can't afford loss." Not so: message loss depends on whether the subscription is **durable**, not on queue-vs-stream. The misconception comes from **Redis Pub/Sub specifically**, which *is* fire-and-forget — no persistence, a subscriber that's down when a message publishes misses it forever; there, the user's intuition is right and Redis **Streams** (persistent, consumer-group offsets, replayable) is the durable answer *within the Redis ecosystem*. But that's a property of Redis Pub/Sub being non-durable, not of pub/sub as a pattern: **durable** pub/sub (Azure Service Bus topics+subscriptions, RabbitMQ durable queues, AWS SNS→SQS) does NOT lose messages — the broker persists per-subscriber until ack. NextAurora's stack already can't lose a message on either side — the **transactional outbox** guards the publish side (event persisted in the same DB transaction as the entity write, dispatched with retry, so "entity saved but event lost" can't happen), and **durable Service Bus subscriptions / RabbitMQ durable queues** guard the consume side (the broker holds the message per-subscriber until ack, so a down service resumes from where it left off). At-least-once delivery means the real risk is *duplication, not loss* — which is why every handler is idempotent (see "Key Conventions: Event handlers must be idempotent"). **Reach for a stream (Kafka, Azure Event Hubs, Redis Streams) only when you need what a durable queue can't give: replay from an offset, multi-day retention, an ordered append-only event log, or N independent consumers each re-reading history at their own pace** — *not* merely "don't lose messages." NextAurora has no such need today (it deliberately deleted the hand-rolled `EventLogs` replay table; any future replay rides Wolverine's own message store). Adding a stream to prevent loss the outbox + durable-queue + idempotency stack already prevents is the same speculative over-engineering the factory-pattern rule warns against. Full transport-selection decision guide (Redis Pub/Sub vs Streams vs RabbitMQ vs ASB vs SNS+SQS vs Kafka/Event Hubs — portable across systems): [docs/messaging-transport-selection.md](docs/messaging-transport-selection.md).
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
- **Non-sargable predicates defeat indexes — fix at write time, not at read time.** A `Where(...)` that wraps the column in a function (`u.Email.ToLower() == x`, `o.CreatedAt.Date == today`) can't use a B-tree index on that column even if one exists — the planner falls back to a full scan. The right fix is at write time: normalize on insert/update (e.g. `EmailNormalized` column populated by the aggregate factory + projected to in `Where(u => u.EmailNormalized == emailNormalized)`), or use a case-insensitive collation at the column level. **Leading-wildcard substring search (`LIKE '%text%'`, `EF.Functions.ILike(p.Name, "%text%")`) isn't B-tree-indexable in any database** — escalate to Postgres `tsvector` full-text search or a dedicated search engine (Elasticsearch/OpenSearch/Meilisearch) when load justifies it. Reference: [CatalogService/Features/SearchProducts.cs](CatalogService/Features/SearchProducts.cs) documents the leading-wildcard trade-off explicitly (intentional; full-text is the named next step if it becomes a bottleneck). The deeper principle: indexes carry a write cost — every insert/update touches every index on the table — so an index the planner can't use is pure overhead, not free defense-in-depth. Adding more indexes isn't a universal speed-up; treat the index list like an interface — each one earns its keep against a real query.
- **Async on request paths**: `await` everywhere. Never `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`. Every async method on a request path takes and propagates `CancellationToken`.
- **Parallelize independent awaits with `Task.WhenAll` — sequential `await`s serialize latency for free.** Async makes a single wait non-blocking; it does not make a *sequence* of waits cheap. When a handler makes N independent I/O calls — N gRPC requests to different services, N HTTP calls to different external APIs, N queries against *different* DbContexts (one per service) — sequential `await`s pay the sum of all latencies, while `Task.WhenAll` pays the max. The anti-shape: `var user = await ...; var orders = await ...; var notifications = await ...;` — three calls that don't depend on each other but execute serially over the request's full latency. The right shape: kick all three off, await once, project the results. Reference: [OrderService/Features/PlaceOrder.cs:93](OrderService/Features/PlaceOrder.cs) — `Task.WhenAll(request.Lines.Select(line => catalogClient.GetProductAsync(line.ProductId, ct)))` is the canonical shape (gRPC fan-out over independent line items, parallelism over the wire, no shared mutable state). The file documents the DbContext safety caveat at lines 89–92 explicitly. **Don't parallelize:** (a) dependent operations where the output of one feeds the input of another (`var user = await ...; var orders = await GetByUserId(user.Id, ct);`), (b) operations sharing the same EF `DbContext` scope — DbContext is NOT thread-safe and parallel EF queries against the same context throw or corrupt state (use `IDbContextFactory<T>` to mint one context per task; see "DbContext is not thread-safe" rule below), (c) operations whose failures must be observed independently — `Task.WhenAll` surfaces only the first exception; the rest run to completion but get swallowed. Use `Task.WhenAll(...)` followed by inspection of each task's `.Exception` (or `Task.WhenEach` in .NET 10) when multi-failure surfacing matters.
- **Long-running work belongs on the message bus, not the synchronous HTTP handler.** If a write path would take more than ~1s (multi-step external API chain, aggregation over thousands of rows, bulk import, report generation), reshape the endpoint as **202 Accepted**: validate + persist a tracking row + publish a Wolverine message + return `202` with the polling key in the body and a `Location` header pointing to a status endpoint. A background handler does the actual work; the client polls (`GET /jobs/{id}`, `GET /orders/{id}`, etc.) or receives a push (SignalR/SSE/email when the job completes). **What counts as "the tracking row":** the aggregate being created can BE the tracking row when its ID is the polling key — that's the `POST /api/v1/orders` shape (the `Order` row IS the tracking record; `GET /api/v1/orders/{id}` IS the polling endpoint). A separate jobs table is only needed when there's no natural aggregate (bulk import of CSV with no per-row entity, report generation with no persistent output). **The synchronous parts commit atomically two ways:** (a) when the endpoint dispatches via `bus.InvokeAsync<T>(command, ct)`, `AutoApplyTransactions` wraps the handler — `SaveChanges` flushes the entity write and Wolverine's staged envelope in one DB transaction; (b) when the endpoint persists + publishes inline (no handler dispatch), use the `BeginTransactionAsync` → work + `PublishAsync` → `SaveChangesAsync` → `CommitAsync` wrap from the Outbox-outside-handler trap (see Observability → Transactional Outbox below). Skipping `SaveChangesAsync` after `PublishAsync` and before `Commit` silently drops the staged envelope. **Why it matters:** the HTTP request holds a thread, a DB connection, and a concurrency-budget slot for the full duration of the handler — a small spike on a slow endpoint can starve the rest of the API. Response time and work duration are different things; the rule is to keep response time bounded. NextAurora already has the full machinery — Wolverine + Service Bus + outbox + saga handlers — so the rule is "use it when a handler would otherwise block." The current `POST /api/v1/orders` is the canonical reference: the handler validates + persists the `Order` + stages `OrderPlacedEvent` + returns `OrderId`, then PaymentService + ShippingService handle downstream work async via the saga. Note that `bus.InvokeAsync<Guid>` awaits the *Place Order handler* synchronously — what makes this the right shape isn't the response code, it's that the handler only does validate-persist-stage and stays sub-second; the minutes-scale work is downstream consumers of the staged event. **Same rule for Wolverine handlers themselves**: a handler body that runs for minutes is the same anti-pattern with a different colored connection — break the work into a follow-up message handler. **Cloud-managed alternatives** when the worker pool needs scale-to-zero or a multi-step durable workflow: AWS SQS + Lambda or Azure Service Bus + Azure Functions for stateless workers; Azure Durable Functions or AWS Step Functions for multi-step orchestration with timers/retries; Temporal for hours-to-days workflows with first-class durable execution. Trade-off is the usual one — less ops, more vendor coupling.
- **Fan-out belongs on the message bus, not in a synchronous handler loop.** A handler that iterates a recipient list inline (`foreach (var follower in followers) await _sender.SendAsync(...)`) holds the request open for N × per-recipient-latency, concentrates the work on one process, and creates traffic spikes that can starve the rest of the system (millions of follower notifications fired by one celebrity post). The right shape: publish **one Wolverine message per recipient** (or per batch of K recipients) and return immediately; per-recipient handlers run in parallel under Wolverine's `MaxDegreeOfParallelism` throttle, set per-handler in `Program.cs` (`opts.LocalQueueFor<SendNotificationRequest>().MaxDegreeOfParallelism(N)`). The throttle gives natural back-pressure — fast producers can't starve slow consumers, and a notification spike doesn't pin a thread or saturate the downstream provider. This is the same principle as "Long-running work belongs on the message bus" applied to fan-out specifically: *accept the work, don't do the work*. Not retroactively violated today (NotificationService receives one inbound event = one outbound notification), but the rule is preventative for any future broadcast-to-N feature (multi-tenant announcements, post-with-followers, abandoned-cart drips, etc.).
- **Pagination**: every list endpoint must paginate with a server-side size cap (≤ 100). Use keyset pagination for large offsets.
- **Bulk ops**: use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` — never load thousands of rows just to mutate or delete them.
- **Optimistic concurrency**: every updatable aggregate must have a concurrency token (Postgres `xmin` or a row-version column). Last-write-wins is not acceptable.
- **Entity IDs use `Guid.CreateVersion7()`, not `Guid.NewGuid()`.** UUID v7 (first 48 bits = Unix-ms timestamp, remaining 74 bits random) is time-ordered, so PK inserts append-extend the B-tree index instead of splitting pages everywhere — kills the index-fragmentation tax that random UUID v4 inserts pay on every write. .NET 9+ API, no third-party package needed, drop-in same `Guid` type. Apply in aggregate factory methods (`Order.Create`, `Payment.Create`, etc.) — the canonical spot to mint domain IDs. **Trade-off:** v7's timestamp is decodable from the ID, so the mint time leaks to anyone holding it. Fine for buyer-scoped resources (IDOR check gates visibility — non-owners can't see the ID at all) and for naturally-public timestamped resources (Product creation time isn't sensitive; often returned in the response anyway). **Don't use v7** for IDs where the mint time IS sensitive (security tokens, admin-only internal references). Existing v4 IDs in the DB stay as-is — v4 and v7 coexist in the same `Guid` column with no migration required; the rule applies to *new* IDs.
- **Outbox atomicity**: the entity write and outbox-row write commit in the same transaction. Prefer one `SaveChanges` call; otherwise use `BeginTransactionAsync` explicitly.
- **`DbContext` is not thread-safe**: parallel queries (`Task.WhenAll`) require `IDbContextFactory<T>` — one context per task. The scoped per-request context handles only sequential work.
- **Structured logging**: use message templates (`"User {UserId} logged in"`) with parameter placeholders, never string concatenation or interpolation. This is also required for the correlation/user/session scope to work.
- **No logging in tight loops**: log summaries (`"Processed {Count} items"`), not per-item lines.
- **DB connection hold time**: open → query → dispose. Don't `await` unrelated work (HTTP calls, message publishes) while a connection is open.
- **Cache invalidation in the write path**: if a handler mutates a cached entity, it must invalidate or update the cache in the same handler — not "later" or "via TTL".
- **Migrations are immutable once applied**: never edit a migration that has run anywhere (dev included). Destructive changes (drop column/table, rename, NOT NULL on existing column) need a multi-step plan, not a single migration.
- **Measure before optimizing**: don't add caching, compiled queries, `ValueTask`, or `AsSplitQuery()` on intuition. Use BenchmarkDotNet for code paths, `dotnet-counters`/k6 for system behavior, `ToQueryString()` for EF.
- **`AsSpan` over `Substring` for zero-allocation slicing — but only on profiled hot paths, and mind the async constraint.** `Substring(...)` allocates a new `string` per call; `AsSpan(...)` returns a `ReadOnlySpan<char>` view over the original with no allocation, and `string.Concat`, `int.Parse`, etc. have span overloads. This is a real win in *synchronous, hot, string-heavy loops* — parsers, formatters, tokenizers, bulk ID/field manipulation. **Two guardrails make it a narrow tool, not a default:** (1) it's a micro-optimization governed by "Measure before optimizing" above — apply it where profiling shows string-allocation pressure, not reflexively; (2) **`Span<T>`/`ReadOnlySpan<T>` is a `ref struct` — stack-only, can't cross an `await` boundary or be captured in a lambda/field**, so it's rarely usable inside NextAurora's async-everywhere request handlers (the compiler will stop you). NextAurora has no such hot path today — its string work is incidental (log templates, IDs), and the bottleneck is always I/O (EF, gRPC, HTTP), never `Substring`. The rule is preventative: *if* a synchronous string-crunching hot path appears and profiling justifies it, reach for `AsSpan`; until then, `Substring` is fine and clearer.
- **Dapper is the sanctioned escape hatch from EF**, not a peer abstraction. Reach for it only when (a) the SQL is provider-specific and doesn't translate cleanly, (b) profiling proves EF is the bottleneck on a hot path, or (c) the query is a SQL aggregation where LINQ obscures intent. Always use `ctx.Database.GetDbConnection()` so Dapper shares the EF connection and any ambient transaction — never open a separate `NpgsqlConnection`/`SqlConnection`. Writes always go through aggregates + EF (Dapper bypasses concurrency tokens, domain validation, and the outbox). Full rationale: [docs/performance-and-data-correctness.md "Decision: when to reach past EF Core (Dapper escape hatch)"](docs/performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch).

## Testing

- Unit tests for domain logic and handlers
- **Test structure — AAA with narrative comments.** Every test is structured as **Arrange → Act → Assert** with `// ARRANGE`, `// ACT`, `// ASSERT` markers (all caps, em-dash explanation on the same line). Each phase carries a *story comment*: explain what's being set up and *why it matters*, what's being called, and what each assertion is verifying. A junior dev should be able to read a single test top-to-bottom and understand the contract being checked + the failure mode being guarded against — without having to read the SUT first. When the ASSERT phase verifies multiple invariants, number them and explain why each matters (especially for security boundaries, idempotency guards, and ordering-sensitive operations like cache-after-save). Trivial happy-path tests can be shorter; security/concurrency/idempotency tests get the full story. Reference templates: [ProductAuthorizationTests.cs](tests/CatalogService.Tests.Integration/ProductAuthorizationTests.cs) (IDOR-prevention + rejected-write invariants), [PaymentFailedHandlerTests.cs](tests/OrderService.Tests.Unit/Application/PaymentFailedHandlerTests.cs) (idempotency under at-least-once delivery), [GetShipmentByOrderHandlerTests.cs](tests/ShippingService.Tests.Unit/Application/GetShipmentByOrderHandlerTests.cs) (IDOR-prevention pattern, unit-tier).
- Integration tests with Testcontainers for infrastructure — `tests/{Service}.Tests.Integration`, booting the real API via `WebApplicationFactory<Program>`. **CatalogService** slice (Postgres + Redis: caching + concurrency token) and **OrderService** slice (SQL Server + Wolverine stubbed transport: outbox, saga handlers, `RowVersion` token) exist; pattern documented in each project's README.
- **Integration tests need Docker.** On macOS, Docker Desktop's socket is at `~/.docker/run/docker.sock`, not `/var/run/docker.sock` — Testcontainers fails fast with `DockerUnavailableException` unless `DOCKER_HOST` points there (or Docker Desktop's "default Docker socket" toggle is on). CI runners have it at the standard path.
- **Fake credentials in test fixtures are suppressed with inline `// gitleaks:allow` markers — there is no project-level gitleaks config.** Some factory fakes have to be protocol-syntax-valid: the fake Azure Service Bus connection string in `OrderApiFactory.cs` / `PaymentApiFactory.cs` / `ShippingApiFactory.cs` is required because `Program.cs` parses `UseAzureServiceBus(GetConnectionString("messaging"))` eagerly at registration time, *before* `DisableAllExternalWolverineTransports()` stubs the transport. The convention: keep the high-entropy base64-encoded **self-labeling** literal (the project's fake `SharedAccessKey` base64-decodes to `fake-shared-key-for-testing-only`) and add `// gitleaks:allow` to the end of the line containing the literal. Gitleaks' default `generic-api-key` rule fires on the entropy of `key=value` strings regardless of value content, so realistic-looking fakes get flagged at PR-scan time. The fix is the **inline marker**, not lowering the fake's entropy below scanner thresholds. **Why no `.gitleaks.toml` lives at repo root:** a path+regex `[[allowlists]]` block was tried and removed — global `[[allowlists]]` requires gitleaks ≥ 8.25.0 (the action ships 8.24.x) and `MatchCondition` defaults to OR not AND, so the block both failed to fire on the pinned scanner version AND would have over-matched if it did. The inline marker is reliable across all 8.x versions, scoped to the exact line, and self-documenting at the call site. Pasting a real key into the same file still trips because the marker only suppresses THAT specific finding line, and pasting the fake literal anywhere without the marker still trips. Do NOT also reproduce the high-entropy literal in CLAUDE.md or any other prose — the scanner walks every diff, including documentation. **The diff-range residue trap:** gitleaks scans every commit in the PR range, not just HEAD, so a fix-in-a-later-commit doesn't suppress findings introduced un-marked in an earlier commit — squash the branch if you hit this. Reference: any of the three factory files for the marker pattern; [.github/workflows/gitleaks.yml](.github/workflows/gitleaks.yml) for the CI wiring.
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
