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

**Feature-file size soft cap — ~300 lines.** Co-locating command record + validator + handler
in one file per use case is the canon (intentional — *the whole feature in one place*). But
"giant file per slice" is a real failure mode. **If a feature file exceeds ~300 lines, consider
extracting the validator or the line-item record into a sibling file**, keeping all of them
in `Features/` (e.g. `PlaceOrder.cs` + `PlaceOrderValidator.cs` + `PlaceOrderLineItem.cs`).
The cap is on *size*, not on *file count per slice* — splitting one slice across multiple
sibling files is fine when the size warrants it. Largest feature today: `PlaceOrder.cs` at 182
lines, well under the cap. Rule earned its keep via the Anton Martyniuk VSA infographic audit
(`.claude/audits/2026-06-08-vsa-when-it-works.md` (`.claude/audits/2026-06-08-vsa-when-it-works.md`)).

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

**File-move discipline — when deleting or renaming a file**, grep the repo for refs to the *old* path and update them in the same PR. Look in: `*.md` (docs, READMEs, INDEX), inline comments in `*.cs`, `Dockerfile*` COPY/ADD lines, `*.csproj` ProjectReference Include, `.github/workflows/*.yml` `run:` blocks, `.coderabbit.yaml` path_instructions. **Same compounding-loop principle as the CLAUDE.md rule above**, just for file structure instead of rule text. Failing to do this is the same drift class as the simplicity refactor fallout — `Dockerfile.catalog` referenced `CatalogService/CatalogService.Api/CatalogService.Api.csproj` for months after PR #31 collapsed CatalogService to a single project, broken silently until someone tried to redeploy. Enforcement is layered: (a) the `check-file-moves.sh` PostToolUse hook prints stale-ref candidates after `git mv` / `git rm`, (b) the CI broken-link audit in `.github/workflows/ci.yml` catches stale markdown links at PR-merge gate, (c) the `.coderabbit.yaml` "**" path_instruction flags missed refs at review time, (d) this rule documents the discipline.

**Doc-and-diagram discipline — docs and diagrams are the review surface, not byproducts.** When reviewers (human or CodeRabbit) want to *observe* what the system does today, they read `docs/architecture.md` and look at `docs/nextaurora-architecture.svg`. If those are stale, every review reasons against a fiction — and drift becomes invisible until someone hits the discrepancy in production. So when code or config changes affect what a doc or diagram *depicts*, the doc/diagram updates in the same PR — or the PR description names the deferred follow-up issue. Concrete pairings (extend as new categories appear):

- **Topology changes** (new service / new transport / new DB / new external dep): `NextAurora.AppHost/AppHost.cs` ↔ `docs/architecture.md` ↔ `docs/nextaurora-architecture.{svg,excalidraw}`
- **Communication-pattern changes** (new gRPC client, new ASB subscription, new endpoint family): same trio plus `docs/messaging-transport-selection.md`
- **EF / caching / outbox / migration mechanism changes**: `docs/performance-and-data-correctness.md` + `docs/ef-core.md` + `docs/cqrs-data-access.md` + sibling diagram (e.g. `docs/efcore-query-write.{svg,excalidraw}`, `docs/transactional-outbox.{svg,excalidraw}`, `docs/hybridcache-flow.{svg,excalidraw}`)
- **Loop / process changes** (new hook, new skill, new CI step, new agent): `docs/dev-loop.md` + `.github/AI_WORKFLOW.md` + `docs/dev-loop.{svg,excalidraw}`
- **Service-request-flow changes** (middleware order, auth flow, correlation propagation): `NextAurora.ServiceDefaults/Extensions.cs` ↔ `docs/architecture.md` Observability section ↔ `docs/service-request-flow.{svg,excalidraw}`

Diagrams are always paired: every `docs/*.excalidraw` ships with a sibling `docs/*.svg`. The `.excalidraw` is the authoritative editable source; the `.svg` is what github.com renders inline (so reviewers see it on the PR page). Editing one without re-rendering the other breaks the pairing — the source no longer matches what reviewers actually see. The render pipeline lives in `.claude/scripts/rebuild-diagrams.sh` (Playwright-driven; reads `.excalidraw`, writes `.svg` + `.png`). Enforcement is layered: (a) the CI `Diagram-pair audit` step fails the build if any `.excalidraw` lacks its `.svg` (or vice versa), (b) the `.coderabbit.yaml` path_instructions for topology-touching files flag missing doc/diagram pairs at review time, (c) this rule documents the discipline. The "is the rendered SVG still in sync with the source?" deeper check isn't mechanically enforced yet (would require the Playwright render in CI); for now, when you edit a `.excalidraw`, run `.claude/scripts/rebuild-diagrams.sh` before committing.

**Presence in the loop, not approval at the gate.** Reviewing the AI's finished diff is not presence — by the time you see it, the agent has already filled gaps you didn't notice. For *non-pattern-conforming* features (new bounded context, novel transport, security model change, multi-step refactor), stay present during implementation: check intermediate state, push back on inferred decisions, course-correct before the diff is too large to read honestly. For *pattern-conforming* features (CRUD endpoint following existing shape, new saga handler matching the canonical pattern), the canon + CodeRabbit + tier-3 mechanical catches do the work — gate review is sufficient. **Rule of thumb: if `/feature-spec` flagged the change as architecturally significant in its Significance Check, treat it as non-pattern-conforming and stay present.** Naming the distinction matters because "I'll review when it's done" is how three days of rework happens.

**Architecturally-significant changes get an `architecture-reviewer` pass BEFORE the PR is opened — not after, not "if I remember."** This is the operational form of "presence in the loop": the agent ([.claude/agents/architecture-reviewer.md](.claude/agents/architecture-reviewer.md)) reads the diff against the canon and returns Must-fix / Should-consider / Aligned. "Significant" = the `/feature-spec` Significance Check would flag it, OR the change adds/removes a dependency or transport, touches 3+ services, alters a cross-cutting pattern (DI, middleware, persistence, messaging, auth), or modifies a Domain aggregate. Findings are addressed or explicitly deferred in the PR body before merge. **A `PreToolUse` hook on `gh pr create` surfaces this reminder at the ship moment** (`.claude/scripts/remind-architecture-review.sh`) — the principle alone proved skippable (the ASB→RabbitMQ swap shipped to PR before review; the review then caught a dead OTel trace source), so the hook is the mechanical catch. Pattern-conforming changes (CRUD endpoint matching an existing shape, an audit-log row) skip the agent — the hook is a reminder, not a block.

**Continue is the verb that gets you in trouble. Build is not.** Prototyping is encouraged — cheap building lets you run more experiments, which is genuine leverage. The discipline is the *stop*, not the *start*: the moment a prototype stops being how you find a return and becomes the whole job (build after build, no return you can point to), it's the stop that matters. For experiments, set a token budget and a stop-time up front. When either runs out, the default answer is "we learned what we needed; we don't continue." Per Kapil Viren Ahuja: *"Nobody approves that. Nobody ever approves that. It approves itself, one month at a time."* The mechanical floor: every experimental branch ends at a defined sunset, even if the work was technically interesting. Carry-debt (maintained, secured code that nobody needed) is real.

All five rules are for everyone working in this repo (humans, AI assistants, future-you). Don't wait to be asked.

## Continuous Rule Encoding (the compounding loop)

The Debugging Discipline rule above covers failures discovered while *debugging*. The same discipline applies to patterns + antipatterns discovered via *any* review surface — architecture-reviewer agent passes, CodeRabbit findings, manual code review, integration-test failures, prod incidents, security audits. **Anything that earns the label "we should never write this again" or "we should always do this when" belongs encoded in `.claude/` config + supporting docs, the same session it's identified.** Otherwise the same finding resurfaces in a future review and the cycle wastes attention.

When you find an antipattern, rule, or specification worth encoding, write to the **smallest set** of these that applies. Default to NOT touching CLAUDE.md unless the rule is genuinely always-on:

1. **CLAUDE.md** — the canonical **always-on** rules layer. **Keep this file lean.** Every byte is loaded into every Claude Code session and every line is cognitive overhead for human readers. Add a rule here ONLY if every session needs it. **One-paragraph maximum per rule.** If a rule needs more than ~6 lines, the rule itself stays as a bolded headline + one-paragraph summary in CLAUDE.md; the detail (the *why*, the failure modes, edge cases, worked examples) moves to a paired doc in `docs/` or the relevant skill, and CLAUDE.md gets a `See [docs/X.md "section"](docs/X.md#section)` pointer. Test: *could this rule + its rationale fit on one screen?* If no, decompose. **CI enforces a soft size budget on this file (warning at 400 lines, hard fail at 500)** to prevent silent bloat.
2. **`.coderabbit.yaml`** `path_instructions` — file-pattern-scoped guidance so CodeRabbit catches future violations at PR-review time without re-deriving the rule. **Most per-file rules belong here, not in CLAUDE.md.** Use the existing `path:` glob entries; add a new one if no fit.
3. **[`.claude/agents/architecture-reviewer.md`](.claude/agents/architecture-reviewer.md)** "Pattern checklist" — a scan rule the agent applies on every review touching the relevant file category. So the next architectural pass catches it before code lands.
4. **[`.claude/skills/`](.claude/skills/) + [`.claude/commands/`](.claude/commands/)** — if the pattern is non-trivial enough to warrant a procedure (multi-step reasoning, specialized vocabulary), it becomes a skill or slash command.
5. **Supporting docs + paired diagrams** ([`docs/architecture.md`](docs/architecture.md), [`docs/performance-and-data-correctness.md`](docs/performance-and-data-correctness.md), [`docs/dev-loop.md`](docs/dev-loop.md), and their sibling `.svg`/`.excalidraw` pairs) — when the *why* deserves more than a one-liner, or when reviewers need a picture to reason against. See "Doc-and-diagram discipline" above.

**Deferral surface (NOT part of the encoding loop).** GitHub Issues (`rule-encoding-deferred` label) tracks findings where code shipped but the encoding hasn't yet. **The issue is a placeholder — a TODO that ensures the encoding eventually happens.** The issue itself is NOT the encoding; the PR that adds the rule to one of the five surfaces above is. Closed issues are not read again in future sessions; the rules live in the surfaces, not in the issue tracker.

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
- **Wolverine `AutoProvision()` is incompatible with the Service Bus emulator — disable it for local dev.** `AutoProvision()` (default on; `Wolverine:AutoProvision` true) provisions topics/subscriptions at startup via the Service Bus **management API** (`ServiceBusAdministrationClient`). The **emulator implements only the AMQP data plane, not the management API**, so every check fails, retries 4×, and the host dies with `BrokerInitializationException: Unable to initialize the Broker asb in time` — a sub-second, deterministic startup crash hitting *every* Wolverine service (catalog has no ASB, so it survives, which makes it look selective). Locally the topology is already declared in `AppHost.cs` (`AddServiceBusTopic`/`AddServiceBusSubscription` write the emulator's config), so provisioning is impossible *and* redundant. **Fix: AppHost injects `Wolverine__AutoProvision=false` into each Wolverine service** (mirrors the integration-test harnesses); in Publish mode against real Azure it stays on to create entities. See [docs/architecture.md](docs/architecture.md) Infrastructure section.

## Communication Patterns

- **Async events** (Azure Service Bus): For workflow orchestration (order -> payment -> shipping -> notification)
- **Durability ≠ replay — don't reach for a stream just to avoid losing messages.** A common misconception is "pub/sub loses the message if a subscriber is down, so use a stream (Kafka/Event Hubs) when you can't afford loss." Not so: message loss depends on whether the subscription is **durable**, not on queue-vs-stream. The misconception comes from **Redis Pub/Sub specifically**, which *is* fire-and-forget — no persistence, a subscriber that's down when a message publishes misses it forever; there, the user's intuition is right and Redis **Streams** (persistent, consumer-group offsets, replayable) is the durable answer *within the Redis ecosystem*. But that's a property of Redis Pub/Sub being non-durable, not of pub/sub as a pattern: **durable** pub/sub (Azure Service Bus topics+subscriptions, RabbitMQ durable queues, AWS SNS→SQS) does NOT lose messages — the broker persists per-subscriber until ack. NextAurora's stack already can't lose a message on either side — the **transactional outbox** guards the publish side (event persisted in the same DB transaction as the entity write, dispatched with retry, so "entity saved but event lost" can't happen), and **durable Service Bus subscriptions / RabbitMQ durable queues** guard the consume side (the broker holds the message per-subscriber until ack, so a down service resumes from where it left off). At-least-once delivery means the real risk is *duplication, not loss* — which is why every handler is idempotent (see "Key Conventions: Event handlers must be idempotent"). **Reach for a stream (Kafka, Azure Event Hubs, Redis Streams) only when you need what a durable queue can't give: replay from an offset, multi-day retention, an ordered append-only event log, or N independent consumers each re-reading history at their own pace** — *not* merely "don't lose messages." NextAurora has no such need today (it deliberately deleted the hand-rolled `EventLogs` replay table; any future replay rides Wolverine's own message store). Adding a stream to prevent loss the outbox + durable-queue + idempotency stack already prevents is the same speculative over-engineering the factory-pattern rule warns against. Full transport-selection decision guide (Redis Pub/Sub vs Streams vs RabbitMQ vs ASB vs SNS+SQS vs Kafka/Event Hubs — portable across systems): [docs/messaging-transport-selection.md](docs/messaging-transport-selection.md).
- **gRPC** (sync): For real-time queries between services (OrderService -> CatalogService product validation). gRPC is versioned separately via `.proto` `package` declarations.
- **REST** (HTTP): For frontend-to-service communication only. URL-segment versioned via `Asp.Versioning.Http` — every endpoint lives under `/api/v{version}/...`. Default version is `1.0`; the version segment is required (`AssumeDefaultVersionWhenUnspecified = false`). **Always use `app.MapV1ApiGroup("Tag", "resource")`** (helper in `NextAurora.ServiceDefaults`) to register a versioned route group — it returns a `RouteGroupBuilder` rooted at `/api/v1/resource` and applies the version + tag in one call. Don't hand-roll `NewVersionedApi(...).MapGroup(...).HasApiVersion(...)` chains — drift across services is the failure mode. To add v2 later, register a side-by-side group with `.HasApiVersion(new ApiVersion(2, 0))`; v1 keeps working untouched.
- **No `IRequestHandler` / `IFooHandler` interface per handler.** Handlers are plain classes (e.g. `PlaceOrderHandler` in `OrderService/Features/PlaceOrder.cs`). Wolverine assembly-scans `Features/` and binds message-type → handler-type directly via `opts.Discovery.IncludeAssembly(...)` — no `IRequestHandler`-style shim is required. The MediatR-style *"one interface per handler for testability / discoverability"* pattern doesn't apply: Wolverine's bus *is* the abstraction, and tests resolve the handler concretely (see the *"Handlers resolved directly in tests must be DI-registered"* rule below). Avoid introducing handler interfaces speculatively — they fail the *"consumer substitution"* test in the same way `IFooRepository` did.
- **Wolverine handler discovery is NOT DI registration — two separate containers.** `opts.Discovery.IncludeAssembly(...)` builds Wolverine's *internal* handler-type map (message-type → handler-type) used by `IMessageBus.InvokeAsync<T>()` / `PublishAsync<T>()`. Wolverine constructs handlers itself via `IServiceScopeFactory` — it never asks `IServiceCollection` for the handler type. Therefore: **`serviceProvider.GetRequiredService<MyHandler>()` throws `InvalidOperationException` unless you also `AddScoped<MyHandler>()`.** Production code paths go through `IMessageBus` and never hit this; the path that does hit it is **integration tests that resolve handlers directly to assert the EF projection SQL** (read-handler integration tests, by far the most common case). Rule: any handler resolved by `GetRequiredService<T>()` in tests must have an explicit `services.AddScoped<T>()` in that service's `AddXInfrastructure` registration. Reference: [OrderService/Infrastructure/DependencyInjection.cs](OrderService/Infrastructure/DependencyInjection.cs) (the `AddScoped<GetOrderByIdHandler>()` / `AddScoped<GetOrdersByBuyerHandler>()` pair). Failure mode that surfaced this gap: `OrderReadProjectionTests` failed in CI with `No service for type 'OrderService.Features.GetOrderByIdHandler' has been registered` after the repository-wrapper refactor — pre-refactor the tests resolved `IOrderRepository` (registered as `AddScoped<IOrderRepository, OrderRepository>()`), and the conversion to handler-resolved tests missed the equivalent registration.
- **Transactional message publishing must use the enlisted context, NOT the constructor-injected `IEventPublisher`.** Only the `IMessageContext` Wolverine injects as a **`HandleAsync` parameter** (in handlers) or an **`IDbContextOutbox`** (in non-handler code) is enlisted in the outbox transaction; a constructor-injected `IMessageBus`/`IEventPublisher` publishes *inline* under Wolverine 6 — before the commit — silently breaking outbox atomicity (events dispatched for entity writes that may roll back). `IEventPublisher` stays only for fire-and-forget / already-committed publishes. Full detail, the worked example, and the war story: [docs/project-decisions.md "Wolverine 5→6 upgrade notes"](docs/project-decisions.md) + [docs/war-story-wolverine6-outbox-atomicity.md](docs/war-story-wolverine6-outbox-atomicity.md).

## Key Conventions

- Commands return the created entity's ID (Guid)
- Queries return DTOs, never domain entities
- Domain entities use factory methods (`Create()`) with validation, not public constructors
- Event handlers must be idempotent
- Use the Outbox pattern for guaranteed event publishing (save entity + event in same transaction)
- All API error responses use RFC 7807 ProblemDetails via `GlobalExceptionHandler` (in `NextAurora.ServiceDefaults`). Never expose internal state, entity IDs, or stack traces to clients — log details server-side and return generic detail with the trace ID
- **Expected business errors throw exceptions at the handler boundary** (e.g. `throw new InvalidOperationException("Insufficient stock for...")` in `OrderService/Features/PlaceOrder.cs`). `GlobalExceptionHandler` translates to RFC 7807 ProblemDetails for the client. **No `Result<T>` / `OneOf<T>` / `ErrorOr<T>` discriminated-union error type** — exception-based control flow is the canon. The Result-pattern is defensible (compile-time exhaustiveness, allocation-cheap), but the trigger to flip would be *profiled exception throw-cost in a hot path* or *a saga step with high expected-failure rate where exception-as-control-flow is uncomfortable*. Neither has surfaced today. Rule encoded after the Anton Martyniuk VSA infographic audit (`.claude/audits/2026-06-08-vsa-when-it-works.md` (`.claude/audits/2026-06-08-vsa-when-it-works.md`))
- Never commit .env files, connection strings, or secrets

## Performance Rules

These are always-on headlines. **Full rationale, edge cases, and worked examples live in [docs/performance-and-data-correctness.md](docs/performance-and-data-correctness.md) "The 14 always-on rules" and the [`dotnet-performance` skill](.claude/skills/dotnet-performance/SKILL.md).** CLAUDE.md keeps the rule + the one-line "what must be true" — not the deep dive.

- **EF Core reads — project in EF, not in memory.** `AsNoTracking()` + `.Select(...)` into a DTO *inside the IQueryable*; the method returns the DTO, not the entity. Writes load the aggregate tracked. Read/write split is the rule, not future cleanup. See [docs/performance-and-data-correctness.md "EF Core reads use AsNoTracking() + projection"](docs/performance-and-data-correctness.md#1-ef-core-reads-use-asnotracking--projection) and [docs/cqrs-data-access.md](docs/cqrs-data-access.md) for the per-architecture-style shapes.
- **No N+1**: use `Include` or projection. Never query inside a `foreach` over results from another query.
- **Non-sargable predicates defeat indexes — fix at write time.** A `Where(...)` that wraps the column in a function (`u.Email.ToLower() == x`) can't use a B-tree index. Normalize on insert/update (an `EmailNormalized` column populated by the aggregate factory) or use a case-insensitive column collation. Leading-wildcard substring search (`LIKE '%text%'`) needs `tsvector`/Elasticsearch/Meilisearch when load justifies it — until then, document the trade-off at the call site. See [docs/performance-and-data-correctness.md](docs/performance-and-data-correctness.md) + `CatalogService/Features/SearchProducts.cs` for the documented trade-off pattern.
- **Async on request paths**: `await` everywhere. Never `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` — banned at build time by `BannedSymbols.txt` and blocked at edit time by the `block-sync-over-async` hook. Every async method takes and propagates `CancellationToken`.
- **Parallelize independent awaits with `Task.WhenAll` — sequential `await`s serialize latency for free.** Caveats: don't parallelize dependent operations, don't share a `DbContext` across tasks (use `IDbContextFactory<T>`), and use per-task exception inspection when multi-failure observability matters. **When the N calls all target the SAME service, a batch endpoint beats client-side `Task.WhenAll`** — one round-trip instead of N parallel ones, and the server can make the whole batch atomic. That's why PlaceOrder's former `WhenAll` fan-out (the old reference shape) was superseded by Catalog's batch `ValidateLines`/`ReserveLines` gRPC methods (issue #71); `WhenAll` remains the right shape for independent calls to *different* services. See [docs/performance-and-data-correctness.md "Parallel awaits"](docs/performance-and-data-correctness.md).
- **Long-running work belongs on the message bus, not the synchronous HTTP handler.** If a write path takes more than ~1s, reshape as **202 Accepted**: validate + persist a tracking row + publish a Wolverine message + return immediately. The aggregate being created can BE the tracking row (the `POST /api/v1/orders` shape). Same rule applies to Wolverine handlers themselves — minutes-scale work belongs in a follow-up message. See [docs/performance-and-data-correctness.md "Long-running work belongs on the message bus"](docs/performance-and-data-correctness.md#long-running-work-belongs-on-the-message-bus-not-the-synchronous-http-handler) for the full pattern + cloud-managed alternatives (Durable Functions, Step Functions, Temporal).
- **Fan-out belongs on the message bus, not a synchronous handler loop.** A handler iterating a recipient list inline holds the request open for N × latency. Right shape: publish one Wolverine message per recipient (or per batch of K), throttle with `MaxDegreeOfParallelism`. See [docs/architecture.md](docs/architecture.md).
- **Pagination**: every list endpoint paginates with a server-side size cap (≤ 100). Keyset pagination for large offsets.
- **Bulk ops**: use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` — never load thousands of rows to mutate or delete them.
- **Optimistic concurrency**: every updatable aggregate has a concurrency token (Postgres `xmin` or a row-version column). Last-write-wins is not acceptable. See [docs/performance-and-data-correctness.md "Optimistic concurrency tokens"](docs/performance-and-data-correctness.md#decision-optimistic-concurrency-tokens).
- **Entity IDs use `Guid.CreateVersion7()`, not `Guid.NewGuid()`.** Time-ordered, so PK inserts append-extend the B-tree index instead of fragmenting it. Apply in aggregate factory methods. Trade-off: v7's timestamp is decodable from the ID — don't use for IDs where mint time is sensitive (security tokens, admin-only refs). Existing v4 IDs coexist with new v7 IDs in the same `Guid` column.
- **Outbox atomicity**: entity write + outbox-row write commit in the same transaction. Prefer one `SaveChanges` call; otherwise `BeginTransactionAsync` explicitly. See [Observability → Transactional Outbox](#transactional-outbox-wolverine) for the non-handler-code trap.
- **`DbContext` is not thread-safe**: parallel queries (`Task.WhenAll`) require `IDbContextFactory<T>` — one context per task.
- **Structured logging**: message templates (`"User {UserId} logged in"`) with parameter placeholders, never concatenation or interpolation. Required for correlation/user/session scope.
- **No logging in tight loops**: log summaries (`"Processed {Count} items"`), not per-item lines.
- **DB connection hold time**: open → query → dispose. Don't `await` unrelated work (HTTP, messaging) while a connection is open.
- **Cache invalidation in the write path**: a handler mutating a cached entity invalidates or updates the cache in the same handler — not "later" or "via TTL".
- **Migrations are immutable once applied**: never edit a migration that has run anywhere. Destructive changes (drop column/table, rename, NOT NULL on existing column) need a multi-step plan.
- **Measure before optimizing**: BenchmarkDotNet for code paths, `dotnet-counters`/k6 for system behavior, `ToQueryString()` for EF. Don't add caching, compiled queries, `ValueTask`, or `AsSplitQuery()` on intuition.
- **`AsSpan` over `Substring`** on profiled synchronous hot paths only. `Span<T>` is a `ref struct` — can't cross `await` boundaries, so it's rarely usable in NextAurora's async-everywhere handlers. The compiler will stop you. Until profiling shows a string-allocation hot path, `Substring` is fine.
- **Dapper is the sanctioned escape hatch from EF**, not a peer abstraction. Reach for it only when (a) the SQL is provider-specific and doesn't translate, (b) profiling proves EF is the bottleneck, or (c) LINQ obscures intent on a SQL aggregation. Always use `ctx.Database.GetDbConnection()` so Dapper shares the EF connection + ambient transaction. Writes still go through aggregates + EF. See [docs/performance-and-data-correctness.md "Dapper escape hatch"](docs/performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch).

## Testing

- Unit tests for domain logic and handlers
- **Test structure — AAA with narrative comments.** Every test is structured as **Arrange → Act → Assert** with `// ARRANGE`, `// ACT`, `// ASSERT` markers (all caps, em-dash explanation on the same line). Each phase carries a *story comment*: explain what's being set up and *why it matters*, what's being called, and what each assertion is verifying. A junior dev should be able to read a single test top-to-bottom and understand the contract being checked + the failure mode being guarded against — without having to read the SUT first. When the ASSERT phase verifies multiple invariants, number them and explain why each matters (especially for security boundaries, idempotency guards, and ordering-sensitive operations like cache-after-save). Trivial happy-path tests can be shorter; security/concurrency/idempotency tests get the full story. Reference templates: [ProductAuthorizationTests.cs](tests/CatalogService.Tests.Integration/ProductAuthorizationTests.cs) (IDOR-prevention + rejected-write invariants, integration-tier), [OrderSagaTests.cs](tests/OrderService.Tests.Integration/OrderSagaTests.cs) (saga consume-side idempotency under at-least-once delivery, integration-tier), [OrderTests.cs](tests/OrderService.Tests.Unit/Domain/OrderTests.cs) (aggregate state-machine invariants, unit-tier — domain-only, no `DbContext`).
- Integration tests with Testcontainers for infrastructure — `tests/{Service}.Tests.Integration`, booting the real API via `WebApplicationFactory<Program>`. Four service slices ship and each runs as its own step in `.github/workflows/ci.yml` so a single-slice failure doesn't mask the rest: **CatalogService** (Postgres + Redis — caching + concurrency token + IDOR), **OrderService** (SQL Server + Wolverine stubbed transport — outbox, saga handlers, `RowVersion` token), **PaymentService** (SQL Server — outbox-in-non-handler wrap, retry/recovery job), **ShippingService** (Postgres — IDOR-by-order saga handlers). Pattern documented in each project's README.
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

Correlation/user/session context is propagated across HTTP + Service Bus by `CorrelationIdMiddleware` (HTTP entry), `ContextPropagationMiddleware` (Wolverine incoming), and `OutgoingContextMiddleware` (outbound). Wired via `opts.AddNextAuroraContextPropagation()` in each service's `Program.cs`. **Full mechanism + headers/baggage mapping + scope behavior + wiring details: see [docs/observability-and-context-propagation.md](docs/observability-and-context-propagation.md).**

**Always-on traps:**

- **HTTP middleware order — strict.** `CorrelationIdMiddleware` runs AFTER `UseAuthentication` (it reads `context.User` to populate `UserId`) and BEFORE `UseAuthorization` (so 401/403 denials log with the user). Canonical order in `MapDefaultEndpoints` (see [Extensions.cs](NextAurora.ServiceDefaults/Extensions.cs)):
  ```csharp
  app.UseExceptionHandler();
  app.UseAuthentication();
  app.UseMiddleware<CorrelationIdMiddleware>();
  app.UseAuthorization();
  ```

- **Wolverine middleware classes must use instance methods.** `opts.Policies.AddMiddleware<T>()` only discovers `Before`/`After`/`Finally` (and `Async` variants) as instance methods on a public class with a public constructor. Static methods aren't discovered → `InvalidWolverineMiddlewareException` at host startup. Suppress S2325 ("should be static") with a `Justification` referencing this rule.

- **Transactional outbox — outside a Wolverine handler, atomicity trap.** `AutoApplyTransactions` only wraps Wolverine handler chains. Code OUTSIDE handlers (`BackgroundService` sweepers, recovery jobs, admin endpoints publishing from non-handler context) needs an explicit wrap: `BeginTransactionAsync` → entity write + `PublishAsync` → **`SaveChangesAsync`** → `CommitAsync`. Skipping the `SaveChangesAsync` between publish and commit silently drops the staged envelope. Reference: [PaymentRecoveryJob](PaymentService/Infrastructure/PaymentRecoveryJob.cs). Full mechanism + canonical wrapper: see [docs/observability-and-context-propagation.md "Outbox outside a Wolverine handler"](docs/observability-and-context-propagation.md#outbox-outside-a-wolverine-handler--atomicity-trap).

- **Structured logging scope hygiene**: never add null/empty keys to scope dictionaries — `if (x is not null) scope["Key"] = x`. Always pass `StringComparer.Ordinal` when constructing `Dictionary<string, T>` (per Meziantou MA0002).

Replay rides Wolverine's `IMessageStore` + DLQ tooling. The previous hand-rolled `EventLogs` table was deleted as dead code post-Wolverine.
