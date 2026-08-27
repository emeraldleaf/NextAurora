# Full-saga demo deployment — plan + tracker

> **Cross-session tracking artifact.** Multi-PR, multi-week effort to stand up
> NextAurora as a portfolio-grade demo deployment running the full
> Order → Payment → Shipping → Notification saga on a **single self-hosted
> Hetzner VPS managed by Dokploy**, with the Stripe gateway stubbed. Pick up
> here when resuming the work.

**Last updated:** 2026-08-25 (Phase 3 rewritten as the three-act demo storyline — #207 engineering view, #208 kill switch; prior: D4 lean profile)

**Current state:** planning; a VPS exists (shared with another, heavier site —
see D4 lean profile), but no NextAurora infra is provisioned on it yet. The
existing CatalogService Fly.io demo is separate and predates this plan (see
"What happens to the Fly demo" below).

---

## Why this doc exists

The CatalogService Fly.io demo proves "I can deploy a .NET service to a PaaS."
A full-saga demo proves "I can architect, build, deploy, and **operate** a
distributed system on infrastructure I manage." Bigger portfolio story. This is
the plan that turns the second story from architecture-on-disk into
infrastructure-running.

The work is several weekends across 5–8 PRs and a ~€7–16/mo fixed VPS spend.
Big enough to need a tracking artifact so each session picks up coherently
instead of re-deriving the plan.

---

## Why Hetzner + Dokploy (the headline decision)

> **Revision 2026-08-27 — Dokploy → `docker compose` + the box's existing Caddy.** Phase 0's
> go/no-go found the target is a *shared* box (`ubuntu-8gb-hel1-1`, 4 vCPU / 7.6 GB) already
> running the riparian stack under plain compose, with **Caddy owning 80/443** and fail2ban/ufw
> hardening. Dokploy's installer needs 80/443 and Docker Swarm mode, and its own
> Postgres/Redis/Traefik cost ~500 MB of the RAM we'd just recovered (Ollama idle-unload,
> ~2.3 GB). Every job the sections below credit to Dokploy is covered on this box without it:
> reverse proxy + automatic HTTPS → Caddy (already doing it); webhook deploys → **pull-based**: a
> systemd timer on the box (`/root/nextaurora/deploy.sh`, every 3 min) runs `compose pull && up -d`
> (a push-style SSH job from Actions times out on the box's SSH allowlist — by design); container templates →
> `docker-compose.{infra,services}.yml` under `/root/nextaurora/`; log view → `docker compose
> logs` (Dozzle if a UI is ever wanted). The headline argument — one box, one Docker network,
> deployed shape ≈ Aspire shape — holds unchanged. Read "Dokploy" below as "the compose stack";
> Phase 0 is done as of this revision (infra tier up: Postgres ×3 DBs, SQL Server Express capped
> at 1.3 GB, Keycloak 26 with realm import + `KC_HOSTNAME=https://auth.emeraldleaf.dev`,
> RabbitMQ 4, Redis; Caddy imports `/root/nextaurora/caddy/*.caddy`). Hostnames:
> `shop` / `auth` / `catalog-api` / `order-api` / `payment-api` `.emeraldleaf.dev`.
> **Next (Turnstile):** the public demo credentials make JWT auth a non-gate against bots, so
> Cloudflare Turnstile (as riparian uses) goes on `POST /orders` and the kill switch — server-
> verified, fail-closed behind an explicit `Turnstile:Enabled`, off in dev/tests.


NextAurora is **already designed as a pile of containers orchestrated by
Aspire** locally — Postgres, SQL Server, Redis, the message broker, Keycloak
all spin up as containers on the dev machine. A Hetzner VPS running
[Dokploy](https://dokploy.com) (an open-source, self-hosted PaaS layer:
reverse proxy + automatic Let's Encrypt HTTPS + webhook deploys + Postgres/Redis
templates) is **that exact shape, deployed.** The deployed environment becomes a
near 1:1 mirror of the Aspire local environment — the strongest dev/prod parity
of any hosting option.

The stack:
- **Hetzner VPS** (cheap, reliable; fixed monthly cost regardless of traffic)
- **Docker images** built straight from .NET, pushed to **GitHub Container
  Registry (GHCR)** by **GitHub Actions**
- **Dokploy** on the VPS handles the PaaS layer: Traefik reverse proxy +
  routing, automatic HTTPS, webhook-triggered deploys (`push → build image →
  GHCR → webhook → Dokploy pulls + redeploys`), container templates for
  Postgres / Redis / etc.

**What this wins over the previous Fly.io + AWS SQS plan:**
- **One box, one Docker network** — every service, database, the broker,
  Keycloak all talk over the internal network. No cross-cloud seam
  (the AWS-SQS-from-Fly awkwardness is gone), no AWS credentials on the compute
  layer, no per-message or egress billing surprises.
- **Always-warm** — no Fly Machine cold start, no Keycloak 30-60s wake. Better
  for a live demo, and it sidesteps the cold-start risk the Fly plan carried.
- **Fixed low cost** — ~€7-16/mo flat (see D4), not variable per-instance.
- **Tightest dev/prod parity** — deployed shape ≈ Aspire local shape.

**The honest trade-offs (you own the box):**
- **Single point of failure.** One VPS down = everything down. Fine for a
  demo/portfolio piece; explicitly NOT how you'd run production. Worth saying
  out loud given the "production-shaped" framing.
- **You're the sysadmin.** OS patching, Dokploy updates, disk/backup
  management. Dokploy handles the PaaS layer (TLS, routing, deploys), so it's
  mostly "keep the OS patched + watch disk space" — but it's real ops Fly
  abstracts away.
- **Security surface.** Internet-facing VPS. Needs Hetzner's (free) Cloud
  Firewall, key-only SSH (no root login), fail2ban. Every IDOR / JWT /
  rate-limit rule encoded in CLAUDE.md is now genuinely live-fire on a box you
  own.
- **No scale-to-zero** — irrelevant here. Scale-to-zero matters when you pay
  per-compute-second; on a flat VPS the cost is fixed and low regardless, and
  always-warm is better for a demo.

---

## Scope

**In:**
- All 5 services running as Docker containers on the Hetzner VPS via Dokploy
- Real databases as containers (both engines — see D1)
- Real messaging as a container (see D3)
- Real Redis as a container
- Keycloak as a container (see D2)
- Real telemetry — deferred; Seq dropped in the lean profile (D4), Dokploy log view + Storefront saga timeline cover the demo (see Phase 3)
- Storefront UI with a working checkout flow (minimum viable, not polished)
- Stripe gateway stubbed; UI banner: *"Payments are stubbed for demo safety"*

**Out:**
- Real Stripe SDK integration (stub retirement — separate, gated on project-purpose change)
- PaymentRecoveryJob retry-with-key (gated on stub retirement; tracked in STATUS.md)
- Production-grade DR / backups / runbooks / HA — single box, this is a demo
- SLAs, on-call, alerting beyond basic uptime

**Why "production-shaped, payment-stubbed":**
- Demonstrates the architecture working end-to-end
- Removes PCI scope entirely
- Honest in the demo copy
- Cheaper than real-payments infra by an order of magnitude

---

## Resolved decisions (revised 2026-05-27 for Hetzner + Dokploy)

### D1 — Database hosting → **keep both engines as containers (SQL Server + Postgres)**

**Changed from the Fly plan.** On Fly, SQL Server hosting was hard, so D1 was
"Postgres-only-for-demo (provider swap on Order + Payment)." On a Hetzner box,
SQL Server is just another Docker container (`mcr.microsoft.com/mssql/server`),
exactly like Aspire runs it locally. So the deployed shape can **keep the
two-engine split** — SQL Server for Order + Payment, Postgres for Catalog +
Shipping — with zero provider-swap work and perfect dev/prod parity.

**Implications:**
- **Eliminates the old Phase 1A entirely.** No `DatabaseProvider` config
  branching, no Postgres migration regeneration, no `xmin`/`RowVersion`
  conditional config. The services deploy with the exact same EF + Wolverine
  config they use in dev.
- **The "two database engines on purpose" architectural story stays true in
  prod** — a stronger portfolio narrative than "two in dev, collapsed to one in
  prod." CLAUDE.md + README need no demo-exception footnote.
- **RAM cost.** SQL Server wants ~2GB RAM minimum — this is the main driver of
  VPS sizing (see D4). A Postgres-only fallback (CX32, ~€7/mo) remains available
  if we want to trade parity for cost, but on Hetzner it would mean doing the
  provider-swap work for no parity benefit — not worth it.
- Both DB containers managed by Dokploy (or via a `docker-compose` stack
  Dokploy supervises); persistent volumes for each.

### D2 — Identity provider → **Keycloak as a Dokploy container**

Same IdP in dev and prod (parity argument unchanged from the Fly plan), but now
a container on the box instead of a Fly Machine. **Strictly better than the Fly
version:** always-warm (no 30-60s Java + realm-import cold start), on the same
internal Docker network as the services.

**Implications:**
- Keycloak container + its own Postgres (a second Postgres container, or a
  separate database in the shared Postgres instance) for Keycloak state.
- Realm import on boot: `--import-realm` + volume-mounted
  `realms/nextaurora-realm.json` (exported from local dev via `kc.sh export`).
- Persistent volume for Keycloak data so user/realm changes survive restarts.
- Two-stage readiness (Keycloak serves HTTP before realm import finishes) —
  Dokploy health check needs to wait for both.
- ServiceDefaults JWT config is already config-driven; deployed
  `Authentication:Authority` points at the internal Keycloak container URL (or
  its Traefik-routed hostname for the browser-facing auth-code flow). Zero
  service code change.

### D3 — Messaging transport → **RabbitMQ in every environment (Azure Service Bus evaluated and removed)**

**Changed from the Fly plan, sub-point resolved 2026-05-27.** On Fly, D3 was
AWS SQS+SNS (free tier, but cross-cloud). On one box, run **RabbitMQ** as a
container on the internal network — no cross-cloud, no AWS credentials, no
egress. RabbitMQ over NATS because it maps cleanly onto the existing Azure
Service Bus topic/subscription topology (exchanges + queues), Wolverine has a
first-class RabbitMQ transport, and the management UI is a nice demo artifact.

**Resolved (2026-06-17): RabbitMQ in *every* environment — dev, CI, and deployed (no config
switch; services call `UseRabbitMq(...)` unconditionally).** The original plan kept the ASB
emulator in dev "to leave the working dev setup untouched" — but that premise proved false: the
ASB **emulator never actually ran the saga** (its subscription admin endpoints return HTTP 500,
and Wolverine's system queues can't auto-provision against it; full arc in #148). RabbitMQ, by
contrast, runs as one clean container, AutoProvisions against the live broker, and the **full saga
flows locally** — verified end-to-end (order → `Shipped` in seconds), giving a working local saga
**and** dev/prod parity. An interim step made the transport config-selectable to prove RabbitMQ
out; the ASB wiring was then **removed entirely**, not kept as a dormant option — per the
codebase's anti-carry-debt rule, a second transport that runs nowhere is speculative coupling, not
optionality. Wolverine still abstracts the broker, so the transport-agnostic claim holds: re-adding
ASB is the same ~5-line block per service (shown below + in git history). Re-add it the day Azure
becomes a real target.

**RabbitMQ licensing (verified 2026-05-27):** the core broker is **MPL 2.0,
free and open-source, self-host at no cost, no vendor lock-in.** Broadcom's
commercial offering — **Tanzu RabbitMQ** (24/7 support, DR, compliance
assistance) — is a separate product for mission-critical shops wanting a
support contract; the OSS broker we'd run as a container is untouched by it and
needs no license. (If even-stronger no-rug-pull governance ever mattered, NATS
is CNCF-foundation-governed — but it doesn't map onto the topic/subscription
model as cleanly, so RabbitMQ wins here.)

**Why the transport choice is low-stakes — Wolverine abstracts it.** The only
transport-specific code is a ~3-5 line block in each event-publishing service's
`Program.cs`. Everything else is transport-agnostic, so neither the RabbitMQ
choice nor a future move to Azure Service Bus (e.g. if NextAurora ever goes
all-Azure) is a lock-in — it's a localized config swap, not a rewrite:

```csharp
// What changes per service (Order, Payment, Shipping, Notification) — current = RabbitMQ:
opts.UseRabbitMq(f => f.Uri = new Uri(conn));        // ← was UseAzureServiceBus(conn)
opts.PublishMessage<PaymentCompletedEvent>()
    .ToRabbitExchange("payment-events");             // ← was .ToAzureServiceBusTopic("payment-events")
opts.ListenToRabbitQueue("payment-orders");          // ← was ListenToAzureServiceBusSubscription("payment-orders-sub", c => c.TopicName = "order-events")

// What does NOT change — transport-agnostic:
opts.PersistMessagesWithSqlServer(db, "wolverine");  // outbox = DB concern
opts.Policies.AutoApplyTransactions();               // outbox staging
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
opts.AddNextAuroraContextPropagation();              // correlation/user/session
// + every handler, the entire saga, all domain logic
```

**Implications:**
- `WolverineFx.RabbitMQ` + `Aspire.Hosting.RabbitMQ` added; `WolverineFx.AzureServiceBus`
  + `Aspire.Hosting.Azure.ServiceBus` removed.
- Each service's `Program.cs` uses RabbitMQ unconditionally (topic→exchange,
  subscription→queue mapping); the AppHost provisions a single RabbitMQ container with the
  management UI. Same `messaging` connection-string name.
- The transactional outbox is **unaffected** — it's a DB concern
  (`PersistMessagesWith{SqlServer|Postgresql}`, which per D1 is SqlServer for
  Order+Payment, Postgresql for Catalog+Shipping), independent of the wire
  transport.
- The RabbitMQ container is provisioned in Phase 0 with its management UI; the
  service transport-config branch lands when each service deploys (Phase 1-2).

### D4 — Cost ceiling + footprint → **lean profile on a shared box (~3–3.5GB idle RAM, ~15–20GB disk); $50/mo hard ceiling**

**Revised 2026-08-22.** The original sizing (CX42, 16GB, ~5.5GB idle) assumed a
dedicated box and maximum parity with the Aspire local stack. The target is now
a VPS **shared with an existing heavy site**, so the footprint has to be as
small as possible *without throwing away the SQL Server work* (the `RowVersion`
concurrency token, the SQL Server outbox/Wolverine store, the two-engine
Testcontainers suites, and the two-engine portfolio story are all load-bearing —
reversing D1 would cost more than it saves). Everything below is **config and
container tuning only; zero code changes.**

#### Lean profile

| Component | Idle RAM | Disk | How it got lean |
|---|---|---|---|
| SQL Server (orders-db, payments-db) | ~1.0–1.3GB | ~2–3GB | `MSSQL_PID=Express` + `MSSQL_MEMORY_LIMIT_MB=1024`; `SIMPLE` recovery on both DBs |
| Keycloak | ~500–600MB | ~0.8GB | `JAVA_OPTS_APPEND=-Xmx512m`, `KC_CACHE=local`, state in the shared Postgres |
| 5 .NET services | ~750MB | ~1.5GB | 256MB memory limit each |
| Postgres — **one instance**, 3 DBs (catalog, shipping, keycloak) | ~200MB | <1GB | `shared_buffers=128MB` |
| RabbitMQ | ~150MB | ~0.3GB | `vm_memory_high_watermark.relative=0.1` |
| Redis | ~50MB | ~0.1GB | `maxmemory 64mb` |
| Dokploy + Traefik | ~200MB | ~1.5GB | — |
| ~~Seq~~ | — | — | **Dropped.** Dokploy's container log view covers the demo; OTLP export stays a local-dev (Aspire dashboard) thing. Revisit only if Phase 3 has runway. |
| **Total** | **~3–3.5GB** | **~15–20GB** | vs. ~5.5GB / ~35GB in the original sizing |

**Disk budget detail:** OS + Docker ~5–6GB, images ~7GB, DB data + volumes
~2–4GB, headroom ~5GB. The two things that grow unbounded and must be capped
on day one: (1) old image layers from webhook redeploys — weekly
`docker image prune -af --filter "until=168h"` (or Dokploy's cleanup toggle);
(2) SQL Server transaction logs — `SIMPLE` recovery model. Never build images
on the box (no build cache, no SDK image): GitHub Actions → GHCR, the box only
pulls.

**Shared-box go/no-go.** Before Phase 0, run `free -h` and `df -h /var/lib/docker`
on the box. Need **~4GB free RAM and ~20GB free disk** *after* the existing site's
steady-state usage. Under that, the fallback is a separate small VPS (Hetzner
CX22, 4GB/40GB, ~€4/mo) — same lean profile, still SQL Server, still no code
changes. Do not try to squeeze below the profile by dropping SQL Server.

**Per-container memory limits are mandatory on a shared box.** The real risk
isn't total usage, it's one runaway container (a SQL Server checkpoint storm, a
Keycloak GC spiral) starving the other site. Every container gets a Dokploy /
compose `mem_limit`; the .NET services additionally get
`DOTNET_GCHeapHardLimit` implied by the cgroup limit (the runtime reads it).

**SQL Server gotcha — the cgroup check.** `sqlservr` refuses to start with
`This program requires a machine with at least 2000 megabytes of memory` and it
evaluates that against the **cgroup limit**, not host RAM. So the container's
`mem_limit` must stay **≥ 2GB** even though the engine is capped to ~1GB via
`MSSQL_MEMORY_LIMIT_MB`. The cgroup limit is a ceiling that's never reached;
the env var is what actually bounds usage. Setting `mem_limit: 1g` to "be safe"
makes SQL Server crash-loop at boot.

**Cost.** The shared box's cost is already sunk; NextAurora's marginal cost is
~€0 (plus ~€1/mo if a separate Hetzner Volume is used to make DB data
separable from the box lifecycle — optional). Hard ceiling stays **$50/mo** as a
guard against accidental over-provisioning if the fallback VPS path is taken.
Set a Hetzner billing alert anyway.

---

## Phases

Four phases now (a single box means a foundational "stand up the box + infra"
phase that everything else builds on). Each phase is independently shippable.

### Phase 0 — Provision the box + Dokploy + infra containers

**Goal.** Stand up the VPS, Dokploy, and every *non-application* container
(databases, broker, Keycloak) before any .NET service deploys. This is the
foundation; nothing application-level happens here.

**Deliverables:**
- [ ] Shared-box go/no-go (per D4): `free -h` + `df -h /var/lib/docker` show ~4GB RAM / ~20GB disk free
      after the existing site's steady state; otherwise fall back to a separate CX22
- [ ] Hetzner Cloud Firewall (allow 80/443/SSH only) — confirm it doesn't break the existing site
- [ ] SSH hardened — key-only, no root login, fail2ban
- [ ] Dokploy installed + its dashboard reachable over HTTPS
- [ ] **One** Postgres container (catalog-db, shipping-db, keycloak-db) + persistent volume,
      `shared_buffers=128MB`
- [ ] SQL Server container (orders-db, payments-db) + persistent volume —
      `MSSQL_PID=Express`, `MSSQL_MEMORY_LIMIT_MB=1024`, `mem_limit` ≥ 2GB (cgroup
      gotcha, D4), both DBs set to `SIMPLE` recovery
- [ ] Redis container (`maxmemory 64mb`)
- [ ] RabbitMQ container + management UI (per D3), `vm_memory_high_watermark.relative=0.1`
- [ ] Keycloak container — realm imported from `realms/nextaurora-realm.json`,
      test users (buyer/seller/admin) present, two-stage health check green,
      `-Xmx512m` + `KC_CACHE=local`, state in the shared Postgres
- [ ] Every container has a `mem_limit` (D4 — mandatory on a shared box)
- [ ] Weekly `docker image prune -af --filter "until=168h"` cron (or Dokploy cleanup toggle)
- [ ] ~~Seq container~~ — dropped in the lean profile (D4); Dokploy log view instead
- [ ] All infra reachable on the internal Docker network; document the internal
      hostnames services will use
- [ ] Cost ledger first entry — marginal cost on the shared box (~€0; ~€1 if a separate volume)

**Risk callouts.**
- SQL Server container RAM appetite — confirm the box doesn't OOM under the full
  infra load before adding services.
- Keycloak two-stage readiness — Dokploy must not route traffic until realm
  import completes.

**Definition of done.** All infra containers healthy on the box, reachable on
the internal network, Keycloak serving the imported realm. No .NET services yet.

### Phase 1 — Order saga visible (Catalog + Order + Storefront)

**Goal.** Deploy the first application services. Show an Order placed,
persisted, and **stalling at payment because PaymentService isn't deployed yet**
— a teaching demo of "what does the saga look like when downstream is absent?"

**Deliverables:**
- [ ] GitHub Actions: build CatalogService + OrderService images → push to GHCR
- [ ] Dokploy apps for Catalog + Order, webhook-triggered deploy on image push
- [ ] Both services wired to their DB containers (Catalog→Postgres,
      Order→SQL Server) + RabbitMQ + Keycloak, all over the internal network
- [ ] gRPC Catalog ↔ Order over the internal network
- [ ] JWT validation against the Keycloak container
- [ ] `DemoMode` flag applied to OrderService (Catalog already has it) — the
      existing `ForwardedHeaders` handling works behind Traefik exactly as it
      does behind Fly's proxy
- [ ] Minimal Storefront deployed — single "place an order" flow
- [ ] End-to-end smoke: log in via Keycloak, place an order, watch it persist +
      stage `OrderPlacedEvent`, see the saga stall (no PaymentService)

**Risk callouts.**
- Traefik routing + the `DemoMode` forwarded-headers path — verify Scalar's
  try-it-out works over HTTPS (same mixed-content gotcha the Fly deploy hit).
- GHCR auth from Dokploy — the webhook pull needs a GHCR read token.

**Definition of done.** Public URL; log in, place an order, see it stall at
payment, all on the Hetzner box.

### Phase 2 — Full saga (Payment + Shipping + Notification)

**Goal.** Deploy the remaining three services so the saga completes end-to-end
with stubbed Stripe.

**Deliverables:**
- [ ] GitHub Actions + Dokploy apps for PaymentService, ShippingService,
      NotificationService (same image→GHCR→webhook pattern)
- [ ] All three wired to their DBs + RabbitMQ + Keycloak on the internal network
- [ ] `DemoMode` flag sweep — apply to Payment, Shipping, Notification
- [ ] Stripe stub remains; optionally richer (latency variation, decline-by-amount)
- [ ] Banner in Storefront: *"Payments are stubbed for demo safety"*

**Risk callouts.**
- RabbitMQ topology — verified locally (live saga: order → Shipped in seconds);
  confirm the deployed broker gets the same exchanges/queues via Wolverine
  AutoProvision (the old ASB topic/subscription names map to exchanges/queues).
- Box load with all 5 services + infra running — watch RAM/CPU headroom.

**Definition of done.** Place an order, watch it flow Payment (stubbed) →
Shipping → Notification end-to-end. Visible in the Storefront saga timeline + Dokploy logs.

### Phase 3 — The demo storyline (what actually sells this)

**Goal.** Make the deployed system *demonstrate itself*. The positioning: nobody
hiring is impressed by an e-commerce site — the differentiator is a
**distributed system you can watch working, and watch surviving failure**. The
pitch in one line: *"Place an order, watch five services coordinate it live —
then kill one mid-flight and watch the system heal itself."* Phase 3 is built
around three acts, in demo order:

- **Act 1 — the saga, visible** (already built, #167): pre-filled demo login,
  place an order, saga timeline animates Placed → Payment → Shipped in seconds
  with the narrator explaining each hop. Free once Phases 1–2 deploy.
- **Act 2 — under the hood** (#207): an *engineering view* in the Storefront
  showing what Act 1 actually was — real event names as they flowed, which
  service handled each, the CorrelationId stitching them, the architecture
  diagram embedded. Makes the demo self-narrating for technical and
  non-technical visitors alike. **Each step carries a pattern caption naming the
  machinery** (transactional outbox, at-least-once delivery, idempotent
  consumers, inbox dedup) under the header: *exactly-once delivery is
  impossible — this system achieves exactly-once processing: push all failures
  toward duplication, then make duplication a no-op.*
- **Act 3 — the kill switch** (#208): a "Kill PaymentService" button
  (DemoMode-gated pause of the Wolverine listener, auto-revive ~60s). Order
  stalls at awaiting-payment; revive; saga completes. The caption is the
  exactly-once narration: while dead, *the event sits in a RabbitMQ durable
  queue*; after revive, *processed once — a redelivery would have been a no-op
  (idempotent handler + inbox dedup). No message lost, none double-processed.*
  The #168/#169 durability hardening turned into theater. This is the closer;
  no tutorial portfolio has it.

**Deliverables:**
- [ ] Act 2 — Storefront engineering view (#207)
- [ ] Act 3 — DemoMode kill switch + auto-revive guardrail (#208)
- [ ] Pre-filled demo credentials on the login screen + "payments are stubbed" banner
- [ ] README "Try the live demo" section with a ~90-second GIF of the three acts
      (for visitors who won't click through)
- [ ] *(Only if runway + RAM headroom — Seq was dropped in the D4 lean profile, ~512MB + growing disk)*
      Wire all services' OpenTelemetry OTLP export to a Seq container
      (`http://seq:5341/ingest/otlp/v1/traces` on the internal network). Seq would be
      added then, not in Phase 0. **Gotcha:** pin `OpenTelemetry.Instrumentation.*`
      versions explicitly in `Directory.Packages.props` — non-stable RC versions
      (e.g. StackExchangeRedis) differ across major bumps.
- [ ] *(Seq-conditional)* Seq dashboards for the saga flow (one timeline per Order, CorrelationId-keyed)
- [ ] **(Enhancement candidate) Live order-status via Server-Sent Events.** The
      baseline order-status UX is polling `GET /api/v1/orders/{id}` — the natural
      client side of the 202 Accepted pattern. The upgrade: an SSE endpoint
      (`text/event-stream`) that pushes status transitions to the Storefront as
      the saga advances, so the buyer watches the order move Placed → Paid →
      Shipped → Notified **live**. Use .NET 10's dedicated SSE result —
      `TypedResults.ServerSentEvents(IAsyncEnumerable<SseItem<T>>)` — which emits
      the correct wire framing (`data:` lines, the `\n\n` frame delimiter, and
      optional `event:`/`id:`/`retry:` fields). Don't hand-roll a raw
      `IAsyncEnumerable<T>` with `Content-Type: text/event-stream` set manually —
      that does NOT produce valid SSE frames on its own and is the easy mistake
      here. A Wolverine handler subscribed to the saga events fans each status
      delta into the stream — in-process, no backplane needed since the box runs
      one instance of each service. Works behind Traefik (SSE is just a
      long-lived HTTP response; ensure proxy response-buffering is off so frames
      flush immediately). **Why it's a candidate, not a requirement:** polling already
      satisfies the demo; SSE is a "watch it happen" upgrade — high demo value,
      zero correctness value. Scope it only if Phase 3 has runway after the
      required deliverables. (SSE is item #7 on the production-readiness checklist
      NextAurora was audited against — one of two gaps; the other, feature flags,
      is deferred — see "Considered and deferred" below.)
- [ ] Security pass — the box is internet-facing; review every IDOR / JWT /
      rate-limit boundary against the live surface
- [ ] Cost confirmation (fixed VPS; just confirm no surprise add-ons)

**Note on rate limiting:** on a single box each service runs as a single
instance, so the in-memory ASP.NET Core limiter is correct — the Redis-backed
swap (CLAUDE.md "Security Requirements → Rate Limiting") only becomes necessary
if a service is scaled to multiple Dokploy replicas. Single-replica is the
default here, so this is N/A unless we deliberately scale out.

**Risk callouts.**
- Storefront UX scope creep — keep it minimal, the demo is the architecture.
  Explicitly out of scope for the demo: seller/admin UIs (#102), which add
  surface but no wow.
- The kill switch is a public, state-changing control — the #208 guardrails
  (DemoMode gate, auto-revive, rate limit) are part of the security pass, not
  optional polish.
- Internet-exposed security surface — the explicit security pass is the
  mitigation. Don't share the URL before it's done.

**Definition of done.** Send the live URL to someone who's never seen the
codebase; they place an order and watch the saga complete in the Storefront saga timeline.

---

## What happens to the existing Fly CatalogService demo

The current [Fly.io CatalogService demo](demo-deployment-story.md) predates this
plan. Two options once the Hetzner Catalog is verified in Phase 1:
- **Retire it** — the Hetzner box becomes the single demo home (one URL, full
  saga). Cleaner story.
- **Keep it** as a "single-service PaaS reference" alongside the Hetzner
  full-saga demo, if the contrast is useful.

Decide in Phase 1; no need to commit now. The Fly demo's `DemoMode` +
`ForwardedHeaders` machinery is reused on Hetzner regardless (Traefik terminates
TLS + forwards HTTP just like Fly's proxy), so the work isn't wasted either way.

---

## Considered and deferred

### Feature flags / feature management

**Decision: deferred — not a Phase 3 deliverable.** Surfaced because feature
flags are a standard production-readiness feature (gradual rollout, A/B testing,
deploy-decoupled-from-release, kill-switch on a misbehaving feature). Considered
honestly and set aside.

**Why deferred for NextAurora:**
- **No audience to roll out to.** Gradual rollout + A/B testing need real
  production traffic segmented across cohorts. A single-developer portfolio demo
  has neither — there's no population to flip 5% of.
- **No deploy/release decoupling pressure.** Feature flags earn their keep when
  you ship code dark and flip it on later, independently of deploy. With one
  developer and Dokploy webhook deploys, *deploy is release* — there's no
  release train to decouple from.
- **`DemoMode` is already enough crude gating.** It's a single config bool that
  surfaces Scalar/OpenAPI + relaxes HTTPS-redirect for the demo. That's a config
  toggle, not a feature-flag *system*, and it's all the gating the demo needs.
- **Flags carry a real maintenance tax.** Per the scaling-failure article
  reviewed earlier, feature flags accumulate into a maze: stale flags linger
  long after rollout, each flag is a branch in the code plus (if remote) a
  config-service lookup that becomes a high-traffic dependency. Adding a flag
  *system* before there's a flag to justify it is the same speculative coupling
  the project deletes elsewhere — cf. the CLAUDE.md factory-pattern rule
  ("don't pre-build the factory while there's only one impl"). A feature-flag
  framework with zero real flags is a layer that buys nothing today.

**The trigger to revisit:** a concrete need to ship a feature dark and flip it
per-cohort, OR run an experiment, OR a kill-switch on a risky feature. When that
lands, [`Microsoft.FeatureManagement`](https://learn.microsoft.com/en-us/azure/azure-app-configuration/feature-management-dotnet-reference)
is the natural .NET choice — config-driven (`appsettings` or Azure App
Configuration), plugs into the existing `IConfiguration`, supports
percentage/targeting filters. Until then, no flag system.

### Server-Sent Events (SSE)

**Promoted to a Phase 3 enhancement candidate** (see Phase 3 deliverables) rather
than deferred — it has real demo value (live saga progression in the Storefront)
and a clean in-process implementation on a single box. Still optional: polling
is the baseline; SSE is scoped only if Phase 3 has runway.

---

## Cost ledger

| Date | Phase | Component | Plan | Monthly cost | Cumulative |
|---|---|---|---|---|---|
| (none yet) | | | | | |

Existing CatalogService Fly demo (separate ledger): ~$0–$5/mo (scale-to-zero, $25 prepaid cap).

---

## Prerequisites before any phase starts

- [x] D3 resolved (2026-06-17): RabbitMQ in every environment (dev/CI/Hetzner);
      Azure Service Bus evaluated and removed
- [ ] Hetzner account + billing alert set
- [ ] GHCR access token for Dokploy's image pulls
- [ ] Domain/subdomain for the demo (for Traefik routing + Let's Encrypt)
- [ ] Branch convention: `deploy/phase-0-vps-infra`, `deploy/phase-1-order-saga`, etc.

---

## Related docs

- [docs/demo-deployment.md](demo-deployment.md) — Recipe for the existing
  single-service (CatalogService) Fly.io deployment. The `DemoMode` +
  `ForwardedHeaders` machinery carries over to the Hetzner Traefik setup.
- [docs/demo-deployment-story.md](demo-deployment-story.md) — Narrative of the
  single-service Fly deployment, gotchas, decisions. Context for what to expect.
- [docs/STATUS.md](STATUS.md) — Cross-session entry point. Has a one-line
  pointer to this doc under "Next" (currently the active multi-PR effort).
- [README.md](../README.md) — Demo URL + scope callout will need updating after
  Phase 2 lands.
