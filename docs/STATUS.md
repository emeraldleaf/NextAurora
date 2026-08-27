# NextAurora — Project Status

> **Read this first when picking up work.** Entry-point doc: where the project is, how to run it, what's source-of-truth where. Keep it short (~100 lines). **Open work lives in [GitHub Issues](https://github.com/emeraldleaf/NextAurora/issues)**, not here.

**Last updated:** 2026-08-26 (all three demo acts built + service images on GHCR; prior: Phase 3 storyline, D4 lean profile)

---

## Where we are

Full microservices architecture (.NET 10, Aspire, Wolverine, EF Core, choreography saga across 5 services) built and runtime-verified. CatalogService demo running on Fly.io at https://catalog-api-demo.fly.dev. Active effort: full-saga Dokploy deployment on a shared VPS ([docs/full-saga-deployment-plan.md](full-saga-deployment-plan.md)). **Everything deployable-without-a-box is done as of 2026-08-26:** Dockerfiles for all five services + `publish-images.yml` pushing `ghcr.io/emeraldleaf/nextaurora-{catalog,order,payment,shipping,notification}` on every main push (#210); and **all three demo acts exist** — Act 1 storefront + saga timeline (#167), Act 2 the live saga canvas (real RabbitMQ topology, per-service colors, viewer-paced replay, persistent narration — #211, most of #207), Act 3 the PaymentService kill switch (DemoMode-gated Wolverine listener pause/revive with 60s self-revive, verified live: pause → order held at `Placed` → revive → `Shipped` — #212, closes #208). **Phase 0 (VPS provisioning) is the critical path and has not started.** Local stack verified 2026-08-26: smoke test green, saga → Shipped, storefront + canvas + kill switch all working in-browser. All five services share one VSA shape after the simplicity refactor + CatalogService Clean→VSA collapse.

**Local-run gotchas learned this cycle:** the Mac's disk hit zero bytes mid-demo (SQL Server + Docker.raw growth) — the stack was pulled down and Docker pruned; keep ≥15 GB free before a local run. Grpc.Tools' `linux_arm64` protoc segfaults in Debian SDK containers on Apple Silicon — build the order image with `--platform linux/amd64` locally (CI/deploy are amd64, unaffected).

**Test tier closed.** Integration coverage for all four services with non-trivial DB/outbox/IDOR behavior (Catalog, Order, Payment, Shipping) + a NetArchTest architecture-tests rung enforcing the dependency rule deterministically across all services' Domain layers. NotificationService stays unit-only (stateless, no DB).

**Code-side loop encoded.** CLAUDE.md is the canonical rule set; `.coderabbit.yaml` mirrors it at PR-review time; `.claude/agents/architecture-reviewer.md` Pattern Checklist applies at local review time; `.claude/skills/` holds procedures; GitHub Issues holds deferred work. Continuous Rule Encoding (CLAUDE.md) is the reflexive step that makes the loop compound.

**In-flight: the merge train.** #166 (Keycloak token policy + fail-closed HTTPS metadata + NU1903 pin) **merged**; next #159 (RabbitMQ transport, ASB removed — full saga verified live: order → `Shipped` in seconds), then #167 (frontend saga timeline + narrator, verified in-browser against the merged stack). Wolverine is on 6.8.0 (upgrade landed).

**Durability hardening landed:** publisher-side topology declaration + `MessagingExchanges`/`MessagingQueues` constants (#168) and durable-inbox/inline listeners (#169) — the no-loss guarantee now holds from first boot on both sides. Dead artifacts from that review are removed on **#174** (in review): the dead-end direct notification queue (#170) and the entire never-injected business-metrics holder class + the emitter-less trace source (#171 — Wolverine's own meter `Wolverine:{ServiceName}` registered via `AddMeter("Wolverine*")` in their place). That review also hardened the tombstone control itself — allowlist exemptions are now group-scoped, not file-scoped (a file-scoped exemption had silently hidden later tombstones). Still open: real-wire failure-injection tests (#68).

---

## Build / test state

- `dotnet build` — clean, 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test` — unit tests green; integration tests (Catalog/Order/Payment/Shipping) green in CI and locally (Docker required); architecture tests green.
- **Coverage** — Codecov main-branch badge: see badge in README. Not gated (relative `threshold: 1%` proposed in [GitHub issue](https://github.com/emeraldleaf/NextAurora/issues?q=is%3Aissue+is%3Aopen+codecov)).
- **Performance baselines not measured yet** — harness exists; no recorded numbers. Tracked in issues.

---

## How to run

1. **Start AppHost:** `dotnet run --project NextAurora.AppHost` (Docker must be running). Wait for the Aspire dashboard at http://localhost:18888 and all resources to reach Running.
2. **Set URLs for the smoke script:** copy `KEYCLOAK_URL`, `CATALOG_URL`, `ORDER_URL` from the dashboard's Resources tab into `.env.smoke` (template at [.env.smoke.example](../.env.smoke.example)). The `.env.smoke` file is gitignored — URLs are dynamic per Aspire run.
3. **Run the automated checks:** `./scripts/smoke-test.sh` — see [scripts/smoke-test.sh](../scripts/smoke-test.sh).

The script verifies service liveness, API versioning enforcement, the Keycloak password-grant auth flow, auth-gate enforcement, and order placement.

### Manual checks the script can't fully automate

```
□ Each DB-using service logs successful migration apply (check service logs in dashboard)
□ Wolverine logs envelope dispatcher start; `wolverine` schema visible in each service DB
□ POST /api/v1/products with seller token → 201   (need a CategoryId; pick one from DB or seed)
□ Aspire dashboard Traces tab: Order → Payment → Shipping → Notification spans, one CorrelationId
□ Mid-saga, query: SELECT TOP 5 * FROM wolverine.outgoing_envelopes ORDER BY received_at DESC
```

---

## What's next

Open work — features, refactors, perf, decisions, tracking issues, deferred encodings — lives in **[GitHub Issues](https://github.com/emeraldleaf/NextAurora/issues)** with `type/*` + `area/*` + `priority/*` labels. The Project board (visual Kanban) is the recommended entry point: **[NextAurora Work board](https://github.com/users/emeraldleaf/projects)**.

Common views:
- **What's next** → board column "Next" or `gh issue list --label priority/next`
- **Active right now** → `gh issue list --label priority/now`
- **By service** → `gh issue list --label area/catalog` (or `area/order` / `area/payment` / `area/shipping` / `area/notification`)
- **Deferred rule encodings** → `gh issue list --label rule-encoding-deferred` (code shipped, CLAUDE.md/.coderabbit.yaml/agent/skill encoding still pending)

To open a new work item: `gh issue create` (uses the [Work item template](../.github/ISSUE_TEMPLATE/work-item.yml)).

---

## Conditional follow-ups (large, not active)

These are *conditional* — only act on them when the project's nature genuinely changes. Filed here (not as open issues) because each depends on a future trigger:

- **If we deploy multi-replica: HybridCache L1 cross-replica invalidation.** Today only Catalog uses `IProductCache` and runs single-replica. The `Microsoft.Extensions.Caching.Hybrid` 10.x package has no backplane; when we go multi-replica, L1 entries go stale across replicas for up to `LocalCacheExpiration` (currently 5 min). Mitigations cheapest → proper: drop `LocalCacheExpiration` to 60s (band-aid) → hand-roll Redis Pub/Sub backplane (~50–100 lines, reuses existing Redis) → migrate to [FusionCache](https://github.com/ZiggyCreatures/FusionCache) (ships a Redis backplane + `.AsHybridCache()` shim). Background: [Milan Jovanović on distributed cache invalidation](https://www.milanjovanovic.tech/blog/solving-the-distributed-cache-invalidation-problem-with-redis-and-hybridcache), [Tim Deschryver on FusionCache backplane](https://timdeschryver.dev/blog/hybridcache-sync-with-fusioncache-backplane).

- **If this stops being a learning project: polyrepo migration sketch.** The monorepo is the right shape for a portfolio. If this becomes a production system with multiple teams: 5 service repos + 2 frontend repos + 1 platform repo, `NextAurora.Contracts` + `NextAurora.ServiceDefaults` published as versioned NuGet on a private feed, contracts-compatibility CI matrix in the platform repo. Estimated effort: ~1 week mechanical (split + CI + private feed + contracts publish) + ~2–3 weeks soak (cross-cutting refactors that quietly relied on monorepo lockstep). DB migrations + the choreography saga / event contracts / outbox / concurrency tokens / HybridCache all carry over unmodified — they're load-bearing architecture, not the monorepo.

---

## Source-of-truth links

| Topic | File |
|---|---|
| Always-on rules every PR follows | [CLAUDE.md](../CLAUDE.md) |
| How code lands — the loop, surfaces, enforcement spectrum | [docs/dev-loop.md](dev-loop.md) |
| Performance / data correctness — *why* every rule exists | [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) |
| Architecture, services, communication patterns | [docs/architecture.md](architecture.md) |
| Visual: full system + 10-step saga in one view | [docs/nextaurora-architecture.svg](nextaurora-architecture.svg) (source: [`.excalidraw`](nextaurora-architecture.excalidraw)) |
| CQRS handler inventory + AsNoTracking strategy | [docs/cqrs-data-access.md](cqrs-data-access.md) |
| Active full-saga deployment plan | [docs/full-saga-deployment-plan.md](full-saga-deployment-plan.md) |
| Portable decision guides | [docs/vsa-vs-clean-architecture.md](vsa-vs-clean-architecture.md), [docs/messaging-transport-selection.md](messaging-transport-selection.md) |
| Functional & non-functional requirements | [docs/BRD.md](BRD.md) |
| Context propagation (CorrelationId/UserId/SessionId) | [docs/context-propagation.md](context-propagation.md) |
| .NET / EF Core deep guidance | [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md) |
