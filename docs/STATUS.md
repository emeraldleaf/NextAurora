# NextAurora — Project Status

> **Read this first when picking up work.** It's the cross-session entry point: where the project is right now, what to do next, and where the deeper docs live. Keep it short (~100 lines). Update it at the start or end of each working session.

**Last updated:** 2026-05-09

---

## Where we are

The architecture is built. Most of it has not been **runtime-verified**.

### Recently landed (commits `b6513f0` + `b79930d` — generic IDE-style messages, see below)

These two commits collectively add:

- **Performance & EF Core skill** ([.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md)) and the [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) guide that captures the reasoning behind every CLAUDE.md performance rule.
- **Optimistic concurrency tokens** on every aggregate (Product, Category, Order, Payment, Refund, Shipment). Postgres `xmin` for Catalog/Shipping; SQL Server `RowVersion` shadow column for Order/Payment.
- **Wolverine transactional outbox** in Order/Payment/Shipping (`PersistMessagesWith{SqlServer|Postgresql}` + `AutoApplyTransactions` + `UseDurableOutboxOnAllSendingEndpoints`). `wolverine` schema in each service's DB.
- **Concurrency exception handling** — `GlobalExceptionHandler` returns 409 on `DbUpdateConcurrencyException` (HTTP path); Wolverine `AddConcurrencyRetry` policy retries 3× with backoff (Service Bus path).
- **Pagination + `CancellationToken`** on the three previously-unbounded list endpoints, server-side cap 100.
- **EventLogs / `/admin/events` deletion** — orphaned post-Wolverine, removed.
- **EF migration tooling** — `IDesignTimeDbContextFactory<T>` per service, initial migrations for all 4, `MigrateDatabaseAsync<T>()` runs at startup in dev only.
- **URL-segment API versioning** via `Asp.Versioning.Http`; `MapV1ApiGroup(tag, name)` helper in `ServiceDefaults` is the canonical registration form.
- **Authentication audit** — JWT Bearer + Keycloak realm wired in ServiceDefaults, `.RequireAuthorization()` + buyer-scope checks on order endpoints. Pre-existing but undocumented; now reflected in all docs.
- **Tier 1 + Tier 2 teaching comments** on ~45 architecturally significant files (domain entities, DbContexts, repositories, handlers, ServiceDefaults helpers, endpoints, gRPC, gateway, DI). CLAUDE.md "Commenting Convention" section makes them durable.
- **Doc consistency sweep** — README, CLAUDE.md, architecture.md, BRD.md, observability.md, event-driven-observability.md, event-catalog.md, copilot-instructions.md, event-replay.md all updated. SellerPortal "Blazor Server" claim corrected (it's a static-file host scaffold).
- **Package bumps** — WolverineFx 5.17 → 5.36.2, OpenTelemetry 1.14.0 → 1.15.x (cleared 4 CVE warnings).

### Recently landed (since the original architectural commits)

- **Smoke-test debugging arc** (commits `1cb5ea8` through `8019891`) — surfaced and captured five Aspire/Wolverine 13.x gotchas: SDK/package version alignment, globally-unique subscription names, `RunAsEmulator()` for Service Bus, `IsPublishMode` gate on App Insights, `WaitFor()` for service-startup gating, Wolverine middleware requires instance methods. Each fix paired with a CLAUDE.md rule update per the Debugging Discipline.
- **Aspire 13.2.4 → 13.3.0** (commit `6f28e2e`) — minor bump, clean.
- **Cross-reference automation** (commit `8644ae5`) — PostToolUse hook surfaces "See CLAUDE.md" markers when CLAUDE.md is edited so paraphrases don't drift.
- **Distributed caching for Catalog** (commit `14e8432`, then upgraded to HybridCache) — `IProductCache` (factory-based `GetOrLoadAsync` + `InvalidateAsync`) backed by `Microsoft.Extensions.Caching.Hybrid` 10.5.0: **L1 in-process MemoryCache + L2 Redis, stampede protection, tag-based invalidation**. `GetProductByIdHandler` reads through the cache; `UpdateProductHandler`/`ReserveStockHandler` invalidate in the write path. 5-min TTL on both tiers as the safety net. Full rationale: [docs/performance-and-data-correctness.md "Decision: distributed read caching with HybridCache"](performance-and-data-correctness.md#decision-distributed-read-caching-with-hybridcache).
- **OpenAPI YAML output** — `app.MapOpenApi("/openapi/{documentName}.yaml")` registered alongside JSON in all five services. Routes: `/openapi/v1.json` and `/openapi/v1.yaml`.
- **Scalar API reference UI** — `Scalar.AspNetCore` 2.14.11 + `app.MapScalarApiReference()` in all five services. Interactive docs UI at `/scalar/v1` in dev, reading from the existing OpenAPI documents. Dev-gated.
- **Dapper escape hatch** — `Dapper` 2.1.72 referenced from the four Infrastructure projects with relational DBs (Catalog, Order, Payment, Shipping). No DI registration; the sanctioned pattern is `ctx.Database.GetDbConnection()` so Dapper shares the EF connection + any ambient transaction. Plumbing only — no current query uses it. Full guidance + when-to-reach-for-it in [performance-and-data-correctness.md "Decision: when to reach past EF Core"](performance-and-data-correctness.md#decision-when-to-reach-past-ef-core-dapper-escape-hatch); CLAUDE.md hard-rule line points to the same.
- **Perf-testing harness** (commit `3d5472a`) — BenchmarkDotNet project at `benchmarks/NextAurora.Benchmarks/` with `OrderFactoryBenchmarks` as starter; k6 smoke at `scripts/k6/smoke.js`. Run instructions in [README "Performance Testing"](../README.md#performance-testing).
- **AWS deployment section** in architecture.md (commits `ea24c6e` + `029c8b8`) — SNS+SQS as the planned production target with a 1:1 topology mapping and a four-phase migration plan. **Status callout at the top: planning, not implemented — the codebase still runs on Azure Service Bus.** System overview diagrams made transport-agnostic.
- **`Handle` → `HandleAsync` rename** across all async handlers (commit `15e11c1`) — CLAUDE.md naming rule compliance.
- **Naming rule fix in `.editorconfig`** (commit `e706edd`) — scoped the `_camelCase` underscore-prefix rule to instance fields only. Constants and `static readonly` use PascalCase per .NET convention. CLAUDE.md "Coding Standards" clarified.
- **Excalidraw architecture diagram** (commit `8dfc5f5`) — 99-element visual at [nextaurora-architecture.excalidraw](nextaurora-architecture.excalidraw). Shows full system, Service Bus topology, databases, 10-step order-placement saga timeline, plus cache-aside and Wolverine outbox callouts. Linked from architecture.md and README.

### Build / test state
- `dotnet build` — clean, 0 warnings, 0 errors.
- `dotnet test` — 134/134 unit tests pass.
- **Integration tests** — CatalogService slice exists (`tests/CatalogService.Tests.Integration`, 4 tests, Testcontainers Postgres + Redis): proves migrations apply, HybridCache caches + invalidates, and the `xmin` concurrency token fires. Runtime correctness of the **saga / outbox / cross-service choreography** is still unverified — that's the next, heavier slice (needs the Service Bus emulator container).
- **Performance baselines not measured yet** — harness exists; no recorded baseline numbers.

---

## Next

**Active item:** smoke-run the system end-to-end.

### How to run

1. **Start AppHost:** `dotnet run --project NextAurora.AppHost` (Docker must be running). Wait for the Aspire dashboard to open and all resources to reach Running.
2. **Set URLs:** copy `KEYCLOAK_URL`, `CATALOG_URL`, `ORDER_URL` from the dashboard's Resources tab into `.env.smoke` (template at [.env.smoke.example](../.env.smoke.example)). The `.env.smoke` file is gitignored — URLs are dynamic per Aspire run.
3. **Run the automated checks:** `./scripts/smoke-test.sh` — see [scripts/smoke-test.sh](../scripts/smoke-test.sh).

The script verifies:
- Service liveness (`/alive` endpoints)
- Versioning enforcement (`/api/products` → 400, `/api/v1/products` → 200)
- Auth flow (Keycloak password grant for buyer1/seller1, JWT decode + sub claim extraction)
- Auth gate enforcement (protected endpoint → 401 without token)
- Order placement (saga entry — only if `PRODUCT_ID` env var is set)

### Manual checks the script can't fully automate

```
□ Each DB-using service logs successful migration apply (check service logs in dashboard)
□ Wolverine logs envelope dispatcher start; `wolverine` schema visible in each service DB
□ POST /api/v1/products with seller token → 201   (need a CategoryId; pick one from DB or seed)
□ Aspire dashboard Traces tab: Order → Payment → Shipping → Notification spans, one CorrelationId
□ Mid-saga, query: SELECT TOP 5 * FROM wolverine.outgoing_envelopes ORDER BY received_at DESC
```

### After the smoke run
Roughly highest-leverage first:

1. **Integration tests — saga/messaging slice.** The CatalogService slice landed (`tests/CatalogService.Tests.Integration`: migrations, HybridCache, concurrency token). Still missing the heavier slice: Wolverine outbox staging in a real transaction, cross-service choreography, the concurrency-retry policy. Needs the Azure Service Bus emulator container. The CatalogService harness is the proven pattern to extend.
2. **Order cancellation flow** — listed in BRD as ORD-08, "Not Yet Implemented".
3. **Saga compensation** — failed-payment / failed-shipment rollback. Larger.
4. **Frontend implementation** — Storefront + SellerPortal scaffolds → real UIs. Big investment.
5. **DTO payload audit** (~1 hour). Walk every `*Dto` and confirm each field is actually consumed by a known client (REST endpoint response, gRPC contract, or cache value). Drop unused fields. Article-triggered: most large-scale .NET slowdowns trace to DTOs that grew faster than their consumers shrank, and the cost is invisible until concurrency amplifies it. Low effort, no architectural risk.
6. **Perf baselines under sustained load** (~2-3 hours initial, then ongoing). The BenchmarkDotNet + k6 harness exists but has never run against the system under concurrent traffic. Build a k6 profile that hits `GET /api/v1/products/{id}` and `POST /api/v1/orders` at realistic ratios + concurrency; capture P50/P95/P99 latency, GC-pause distribution (`dotnet-counters` for `System.Runtime`), connection-pool saturation point, and HybridCache hit ratio. Without numbers we can't tell the difference between "fast enough" and "lucky so far." Pairs naturally with integration tests since Testcontainers gives us a reproducible target.

---

## Open issues / known gaps

- **Two recent commits have generic messages** (`Refactor code structure for improved readability and maintainability`). The architectural detail is recoverable from this doc + [performance-and-data-correctness.md "What changed when"](performance-and-data-correctness.md#what-changed-when), but `git log` alone won't tell the story. Future commits should use real messages.
- **Production migration deploy step** not yet automated. Tooling exists; deploy automation doesn't. See [perf guide](performance-and-data-correctness.md#resolved-migration-tooling-wired-up).
- **Integration tests** — CatalogService slice exists; outbox semantics, concurrency-retry behavior, and saga choreography are still uncovered (see "After the smoke run" item 1).
- **Service-to-service auth** (mTLS or per-service tokens) not configured. Fine inside the Aspire mesh; matters in production.

---

## If we deploy multi-replica: HybridCache L1 cross-replica invalidation

Conditional follow-up — only matters once we deploy more than one replica of any service that uses `IProductCache` (today only Catalog).

**The problem.** `Microsoft.Extensions.Caching.Hybrid` 10.x has no backplane. When replica A invalidates a `ProductDto`, replicas B/C continue serving the stale value from their own in-process L1 for up to `LocalCacheExpiration` (currently 5 min). The API proposal for a pluggable backplane ([dotnet/extensions#5517](https://github.com/dotnet/extensions/issues/5517)) was closed as "NOT ready for implementation" — not coming soon.

**Mitigation, cheapest first:**
1. **Drop `LocalCacheExpiration` to 60s** in [HybridProductCache.cs](../CatalogService/CatalogService.Infrastructure/Caching/HybridProductCache.cs). One-line change. Bounds cross-replica staleness at 60s. We lose part of the L1 win for the warm-but-aging tail of entries but keep the hot-entry win and the L2 win. **This is the right move for "ship multi-replica with reasonable consistency."**
2. **Migrate to [FusionCache](https://github.com/ZiggyCreatures/FusionCache)** if 60s isn't tight enough. FusionCache ships a Redis pub/sub backplane that publishes invalidations to all replicas — drop-in functional replacement with the consistency story we originally wanted from HybridCache. Wiring change is moderate: swap package, retarget the `IProductCache` adapter, verify metrics still flow through OTel. Estimate ~half day plus a chaos test. The cache *seam* (`IProductCache`) stays the same; handlers don't change.

**Filed here, not in "After the smoke run,"** because this only matters once there's an actual multi-replica deployment. Don't pre-optimize for cross-replica before there's a real cross-replica. Background reading: [Tim Deschryver: FusionCache backplane synchronizing HybridCache](https://timdeschryver.dev/blog/hybridcache-sync-with-fusioncache-backplane).

---

## If this stops being a learning project: polyrepo migration sketch

The current monorepo is the right shape for a portfolio — full architecture in one `git clone`, lockstep changes across services, simple local dev. If this ever becomes a production system with multiple teams, the split would look like:

**Target shape:**
- 5 service repos (`nextaurora-{catalog,orders,payments,shipping,notifications}`) — each owns its tests and migrations
- 2 frontend repos (`nextaurora-{storefront,seller-portal}`)
- 1 platform repo (`nextaurora-platform`) — `AppHost`, integration tests, architecture docs, deploy manifests, the Excalidraw diagram
- `NextAurora.Contracts` and `NextAurora.ServiceDefaults` → versioned **NuGet packages** on a private feed (GitHub Packages, Azure Artifacts)

**Contracts strategy:** single `NextAurora.Contracts` NuGet package, SemVer-pinned by each consumer, with a **compatibility-matrix CI job** in the platform repo that builds each service against latest contracts and reports drift. Per-bounded-context contract packages and schema-registry-driven generation are stronger options, but earn their weight only at much larger team scale.

**The five things that actually change:**
1. **Local dev breaks unless re-engineered.** `dotnet run --project NextAurora.AppHost` builds everything from source today; polyrepo means either sibling-checkout + path-references OR pre-built container images + `AddDockerImage(...)`. Most shops do both modes.
2. **Cross-cutting refactors become multi-PR migrations.** "Add a field to `OrderPlacedEvent`" goes from one PR to: contracts bump → publish → update each consumer → integration-test in platform. Intentional friction, but you pay it on every change.
3. **`ServiceDefaults` drift.** Every service ends up on a different version. Mitigation: a "minimum supported `ServiceDefaults`" version pin enforced in CI + Dependabot.
4. **Code search loses cross-repo navigation.** "Where is `OrderPlacedEvent` handled?" stops being a single grep. Need sourcegraph, GitHub cross-repo search, or central tooling.
5. **Integration tests can't live with one service.** Platform repo owns them. Aspire-driven with pre-built images is the cleanest harness.

**What doesn't change:** DB migrations stay with their owning service (already correctly organized), the choreography saga / event contracts / outbox / concurrency tokens / HybridCache all carry over unmodified — they're the load-bearing architecture, not the monorepo.

**Estimated effort:** ~1 week mechanical (split + CI setup + private feed wiring + contracts package publish) plus ~2-3 weeks soak (find the cross-cutting refactors that quietly relied on monorepo lockstep). Single biggest investment: the contracts-compatibility CI matrix. Everything else is mechanical.

This is filed here, not in "After the smoke run," because it's *conditional* — only do it when the project's nature actually changes. Premature polyrepo splits add maintenance overhead without unlocking team-scale benefits.

---

## Source-of-truth links

| Topic | File |
|---|---|
| Always-on rules every PR follows | [CLAUDE.md](../CLAUDE.md) |
| Performance / data correctness — *why* every rule exists | [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) |
| Architecture, services, communication patterns | [docs/architecture.md](architecture.md) |
| Visual: full system + 10-step saga in one view | [docs/nextaurora-architecture.excalidraw](nextaurora-architecture.excalidraw) |
| CQRS handler inventory + AsNoTracking strategy | [docs/cqrs-data-access.md](cqrs-data-access.md) |
| Functional & non-functional requirements | [docs/BRD.md](BRD.md) |
| Context propagation (CorrelationId/UserId/SessionId) | [docs/context-propagation.md](context-propagation.md) |
| .NET / EF Core deep guidance | [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md) |
