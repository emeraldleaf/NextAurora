# Full-saga demo deployment — plan + tracker

> **Cross-session tracking artifact.** Multi-PR, multi-week effort to stand up
> NextAurora as a portfolio-grade demo deployment running the full
> Order → Payment → Shipping → Notification saga over real cloud infrastructure
> with the Stripe gateway stubbed. Pick up here when resuming the work.

**Last updated:** 2026-05-27 (plan created)

**Current state:** planning. No new infrastructure provisioned yet beyond the
existing CatalogService Fly.io demo.

---

## Why this doc exists

The CatalogService Fly.io demo proves "I can deploy a .NET service to the cloud."
A full-saga demo would prove "I can architect, build, deploy, and operate a
distributed system." Big difference for a portfolio piece. This is the plan that
turns the second story from architecture-on-disk into infrastructure-running.

The work is several weekends across 5–8 PRs and an ongoing ~$15–40/mo
infrastructure spend. Big enough to need a tracking artifact so each session
can pick up coherently instead of re-deriving the plan.

---

## Scope

**In:**
- All 5 services deployed to Fly.io
- Real databases (split TBD — see decisions D1)
- Real messaging (transport TBD — see decision D3)
- Real Redis (Upstash or Fly Redis)
- Hosted identity (TBD — see decision D2)
- Real telemetry (App Insights or OpenTelemetry ingestion)
- Storefront UI with a working checkout flow (minimum viable, not polished)
- Stripe gateway stubbed; UI banner: *"Payments are stubbed for demo safety"*

**Out:**
- Real Stripe SDK integration (stub retirement — separate, gated on project-purpose change)
- PaymentRecoveryJob retry-with-key (gated on stub retirement; tracked in STATUS.md)
- Production-grade DR / backups / runbooks — this is a demo, not a business
- SLAs, on-call, alerting beyond basic uptime

**Why "production-shaped, payment-stubbed":**
- Demonstrates the architecture working end-to-end
- Removes PCI scope entirely
- Honest in the demo copy
- Cheaper than real-payments infra by an order of magnitude

---

## Phases

Three phases, each independently shippable. If life happens between phases,
each phase ends with something demoable.

### Phase 1 — Order saga visible (Catalog + Order + minimal Storefront)

**Goal.** Land the deployment pattern with the smallest possible footprint.
Show an Order being placed, persisted, and **stalling at payment because
PaymentService isn't deployed yet** — itself a teaching demo of "what does the
saga look like when downstream isn't available?"

**Why this is the right starting point.**
- First-deployment gotchas (Dockerfile cold start, EF migration on boot, JWT
  config, gRPC over TLS, secrets binding) get caught once, not three times.
- Visible milestone: a deployed Order saga that stalls is itself a portfolio
  piece. If something interrupts the work, you've landed something coherent.
- Cost validation: real bills for a week before committing to Phase 2's larger
  footprint.

**Deliverables.**
- [ ] `OrderService` Dockerfile + `fly.toml` + GitHub Actions deploy workflow
      (mirrors CatalogService's pattern at [Dockerfile.catalog](../Dockerfile.catalog))
- [ ] `OrderService` Fly app + database hosting per decision D1
- [ ] Minimal Storefront deployed — single "place an order" flow
      (the existing Blazor WASM scaffold or a simpler Razor Pages slice — decide
      during phase, not now)
- [ ] JWT validation working against deployed identity provider (decision D2)
- [ ] gRPC Catalog ↔ Order working over TLS in the deployed environment
- [ ] `DemoMode` flag applied to `OrderService` (mirrors CatalogService's
      pattern: gates Scalar + OpenAPI + skip-HTTPS-redirect + migrate-on-startup)
- [ ] Cost ledger first entry; verify ≤ $15/mo target

**Risk callouts.**
- **D1 (SQL Server hosting) is the biggest cost lever.** Azure SQL serverless
  with auto-pause is ~$15-30/mo for two DBs; Postgres-only-for-demo is free
  with Fly Postgres but breaks the "two engines on purpose" architectural
  story in the deployed shape (would need a footnote in the README + Wolverine
  outbox provider swap).
- **D2 (identity)** — deploying Keycloak is operationally heavy. Hosted
  alternatives (Auth0, Azure AD B2C) are simpler but introduce vendor lock-in.
- **First-deployment Dockerfile gotchas** — cold start can be 10+ seconds for
  a fresh Fly Machine; EF migration on boot adds to that. May need a warmup
  endpoint or accept it in demo copy.

**Definition of done.** Place an Order via the Storefront, see it persist in
the Order DB, see `OrderPlacedEvent` staged in `wolverine.outgoing_envelopes`,
see the saga stall because PaymentService isn't deployed yet. All over real
cloud infrastructure with real JWT auth. Reachable via public URL.

### Phase 2 — Full saga (Payment + Shipping + Notification)

**Goal.** Light up the remaining three services so the saga completes
end-to-end with stubbed Stripe.

**Deliverables.**
- [ ] Same deployment pattern (Dockerfile + fly.toml + GitHub Actions) for
      PaymentService, ShippingService, NotificationService
- [ ] Real Azure Service Bus namespace OR alternative transport (decision D3)
- [ ] `DemoMode` flag sweep — apply the existing CatalogService pattern to
      PaymentService, ShippingService, NotificationService
- [ ] Stripe stub remains; optionally make it slightly more interesting
      (latency variation, decline-by-amount) for a richer demo
- [ ] Banner in Storefront UI: *"Payments are stubbed for demo safety"*

**Risk callouts.**
- Messaging cost: real ASB Basic ~$10/mo + per-message. Alternative: NATS or
  RabbitMQ on Fly (cheaper but more ops surface), or AWS SQS+SNS (free tier
  generous, but Wolverine reconfig).
- Cold-start latency compounds across 5 services. May need to keep one or
  more services warm (small extra cost) or document the first-request delay.

**Definition of done.** Place an Order, watch it progress through Payment
(stubbed) → Shipping → Notification end-to-end. View it in observability
tooling. Demoable in ≤ 60 seconds after warmup, or document the cold-start
delay honestly in the demo copy.

### Phase 3 — Polish (observability + ops + UX)

**Goal.** Make it actually demoable to other humans.

**Deliverables.**
- [ ] Real telemetry endpoint wired (App Insights or OpenTelemetry collector)
- [ ] Dashboards for the saga flow (one timeline per Order, CorrelationId-keyed)
- [ ] Storefront UX polished enough to live-demo (minimal, not feature-rich)
- [ ] `README` "Try the live demo" section with auth credentials, expected
      flow, known cold-start gotchas
- [ ] Cost dashboard + monthly review cadence
- [ ] **Rate-limit audit + Redis-backed swap if multi-instance.** Catalog
      search + Payment process both use ASP.NET Core's built-in in-memory
      limiter. If any service runs 2+ Fly Machines for resilience after
      Phase 2, the in-memory limit silently multiplies by N — bypassable.
      Swap those endpoints to a Redis-backed limiter using the Redis
      instance already present for HybridCache. See CLAUDE.md
      "Security Requirements → Rate Limiting" for the rule details.

**Risk callouts.**
- Storefront UX scope creep — keep it minimal, the demo is the architecture
  not the UI.
- Internet-exposed = real security surface. Every IDOR / JWT / CSRF rule
  encoded in CLAUDE.md is now live-fire, not theoretical. Worth an explicit
  security pass before sharing the URL.

**Definition of done.** Send the live URL to someone who's never seen the
codebase; they can place an order and watch the saga complete.

---

## Resolved decisions (2026-05-27)

### D1 — SQL Server hosting → **Postgres-only-for-demo (provider swap)**

Deployed shape uses Postgres for all four services with state. Dev environment
keeps the two-engine split (SQL Server for Order + Payment, Postgres for
Catalog + Shipping) — the "two engines on purpose" architectural story is
still genuine in the dev/learning context; the deployed shape gets a README
footnote explaining the demo exception.

**Implications:**
- **Provider swap on Order + Payment.** Both services need
  `PersistMessagesWithPostgresql` (currently `PersistMessagesWithSqlServer`)
  and `xmin` concurrency tokens (currently `RowVersion` shadow column).
  Config-driven via a `DatabaseProvider` setting so dev keeps SQL Server.
- **Postgres-flavored migrations** for Order + Payment. Either regenerate
  from scratch against the Postgres provider, or maintain parallel migration
  histories per provider (EF Core supports `ContextType` partitioning).
- **README footnote.** "Two database engines on purpose" gets a "demo
  deployment exception" callout linking here. CLAUDE.md unchanged — the
  rule is still genuine for dev/learning.
- Free with Fly Postgres → keeps Phase 1 cost ≤ $15/mo target.

### D2 — Identity provider → **Keycloak self-hosted on Fly**

Same IdP in dev and prod. The local Aspire-managed Keycloak container already
imports `realms/nextaurora-realm.json` on boot — the deployed Keycloak does
the same with the same realm export. One IdP, one realm, two environments.
Removes a class of "works in dev, breaks in prod" bugs around realm shape,
claim names, and test users.

**Implications:**
- **Keycloak Fly app + Postgres database** for Keycloak's own state
  (~$5-10/mo for the Fly Machine; Postgres free with Fly's shared instance).
- **Realm import on boot** via `KC_DB_*` + `KEYCLOAK_ADMIN*` env vars +
  `--import-realm` startup flag pointing at the volume-mounted realm export.
- **Persistent volume** for Keycloak's data dir so realm changes (new users,
  password resets) survive Machine restarts.
- **Two-stage readiness probe** — Keycloak serves HTTP before the realm
  import completes; health check has to know about both. Documented gotcha
  in the article.
- **Boot cost** — Keycloak is a Java app, ~512MB RAM minimum, ~30-60s cold
  start. Scale-to-zero is unrealistic; keep one Machine always-on or accept
  the first-request wait.
- **Realm export workflow** — `kc.sh export` from local Keycloak → commit
  the JSON → deploy reads it. Document this in the deployment recipe.
- ServiceDefaults JWT config is already config-driven; deployed
  `Authentication:Authority` points at the Fly Keycloak URL. Zero code
  change in any service.

### D3 — Messaging transport → **AWS SQS+SNS free tier**

Free tier covers 1M req/mo, comfortably more than demo volume. Wolverine has
an AWS provider package.

**Implications:**
- AWS account setup + IAM user for the demo with SQS/SNS-only permissions.
- `Wolverine.AmazonSqs` (and SNS support if needed) added to
  `Directory.Packages.props`.
- `Program.cs` in each event-publishing service: `UseAzureServiceBus(...)` →
  `UseAmazonSqs(...)`. Config-driven so local dev keeps the ASB emulator.
- Topic/subscription topology recreated as SQS queues + SNS topics. The
  existing per-service subscription names (e.g. `notify-orders-sub`) map to
  SQS queue names; topics (`order-events`, `payment-events`) map to SNS
  topics.
- AWS credentials wiring: env vars in Fly Machine secrets + dev secrets via
  `dotnet user-secrets` locally if cross-stack work is needed in dev.

### D4 — Cost ceiling → **$30/mo target, $50/mo hard ceiling**

- Phase 1 target: ≤ $15/mo (Postgres free with Fly + Auth0 free tier + SQS
  free tier + small Fly Machines)
- Phase 2 target: ≤ $30/mo (additional Fly Machines for Payment + Shipping +
  Notification + Storefront)
- Phase 3 target: unchanged
- **Hard ceiling: $50/mo.** If actual costs exceed this: stop, audit, decide
  before resuming. Set Fly.io spend cap to $50 before provisioning anything.

## Implementation order

Given the resolved decisions, Phase 1 splits naturally into three sub-PRs.
Each is independently shippable.

### Phase 1A — Postgres provider swap (code only, no deployment)

**Goal.** Land the dual-provider config plumbing in `main` so Phase 1C's
deployment can simply set `DatabaseProvider=Postgres` and pick up the right
EF + Wolverine + concurrency-token combo.

**Deliverables:**
- [ ] `DatabaseProvider` config setting (defaults `SqlServer` in dev, override
      to `Postgres` in deployed `appsettings.Production.json` or via env var)
- [ ] Order + Payment `Program.cs`: branch on `DatabaseProvider` for `AddDbContext`
      + Wolverine outbox provider
- [ ] Order + Payment EF migrations re-generated against the Postgres provider
      (parallel migration history or `ContextType` partitioning)
- [ ] Order + Payment concurrency-token config branches on provider
      (`RowVersion` for SqlServer, `xmin` for Postgres)
- [ ] Integration tests verify both code paths build + run (the existing
      OrderService.Tests.Integration uses SQL Server Testcontainer; add a
      Postgres Testcontainer slice for the new path)
- [ ] README footnote: "demo deployment uses Postgres-only as an exception"

### Phase 1B — Deploy Keycloak self-hosted on Fly (infrastructure, no service code change)

**Goal.** Stand up the deployed identity provider before any application
service tries to validate JWTs against it. ServiceDefaults already handles
JWT validation config-driven, so this phase is pure infrastructure.

**Deliverables:**
- [ ] Fly Postgres database provisioned for Keycloak's state
- [ ] `Dockerfile.keycloak` + `fly.keycloak.toml` + GitHub Actions deploy
      workflow for Keycloak
- [ ] Persistent volume for Keycloak data dir
- [ ] Realm export workflow documented in [demo-deployment.md](demo-deployment.md):
      `kc.sh export --realm nextaurora` from local dev → commit the JSON →
      deploy reads it on boot
- [ ] Two-stage readiness probe (Keycloak serves HTTP before realm import
      completes; both have to be green before traffic flows)
- [ ] Test users from local realm (buyer, seller, admin) imported in deployed
      Keycloak
- [ ] Documented public Keycloak URL for future services to point at via
      `Authentication:Authority` env var
- [ ] Cost verification — Fly Machine for Keycloak + free Postgres ≤ $10/mo

**Risk callouts.**
- Keycloak cold start is real (~30-60s for Java + realm import). Either keep
  one Machine always-on (cost) or accept the first-request wait in demo copy.
- Realm export format can change across Keycloak versions; pin the version
  in `Dockerfile.keycloak` and document the export-format-version dependency.

### Phase 1C — Deploy Order + minimal Storefront

**Goal.** Phase 1's original visible-saga goal: deployed Order with auth.

**Deliverables:** as already listed in Phase 1 above, plus:
- [ ] Fly Postgres provisioned for OrderService
- [ ] OrderService deployed with `DatabaseProvider=Postgres` and
      `Authentication:Authority` pointing at deployed Keycloak (1B)
- [ ] Minimal Storefront deployed
- [ ] End-to-end smoke test: log into Storefront with Keycloak test user,
      place an order, watch it stall at payment

---

## Cost ledger

| Date | Phase | Component | Plan | Monthly cost | Cumulative |
|---|---|---|---|---|---|
| (none yet) | | | | | |

Existing CatalogService demo (separate ledger): ~$0–$5/mo on Fly.io (scale-to-zero, $25 prepaid cap).

---

## Prerequisites before any phase starts

- [ ] All four open decisions (D1–D4) resolved and captured in this doc
- [ ] Fly.io account billing set up with a hard spend cap matching D4
- [ ] Identity provider account created per D2
- [ ] Messaging provider account created per D3 (if D3 ≠ Fly-native)
- [ ] Branch convention agreed: `deploy/phase-1-order-saga-visible`, etc.

---

## Related docs

- [docs/demo-deployment.md](demo-deployment.md) — Recipe for the existing
  single-service (CatalogService) Fly.io deployment. Phase 1 builds on this.
- [docs/demo-deployment-story.md](demo-deployment-story.md) — Narrative of the
  single-service deployment, gotchas, decisions. Useful context for what to
  expect in Phase 1.
- [docs/STATUS.md](STATUS.md) — Cross-session entry point. Has a one-line
  pointer to this doc under "Open issues."
- [README.md](../README.md) — Demo URL + scope callout will need updating
  after Phase 2 lands.
