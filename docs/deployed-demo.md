# The deployed demo — as built

**This deployment exists to be a demo.** It is a real distributed system — real broker, real
outbox, real per-service databases, real TLS — deliberately running with demo-grade
operational choices, each listed [below](#demo-grade-by-choice-what-production-would-do-differently)
with what production would do instead. The plan and its decision history (Fly.io → Dokploy →
compose, D1–D4) live in [full-saga-deployment-plan.md](full-saga-deployment-plan.md); the
box-level file map lives in [deploy/vps/README.md](../deploy/vps/README.md); this doc is the
*as-built* picture: what is running, how a request flows, how code reaches the box, and what
the `DemoMode` flag actually gates.

## What is live

| Surface | URL |
|---|---|
| Storefront (React SPA) | https://shop.emeraldleaf.dev — `buyer1`/`buyer1` |
| Identity (Keycloak 26) | https://auth.emeraldleaf.dev/realms/nextaurora |
| Catalog / Order / Payment APIs | `https://{catalog,order,payment}-api.emeraldleaf.dev` — interactive docs at `/scalar/v1` |

One shared 4-vCPU / 8 GB Hetzner box (it also hosts an unrelated app), everything in Docker
on one internal network. Shipping and Notification run but are internal-only — nothing
public calls them; they consume from RabbitMQ.

## Request flow

```
browser ──HTTPS──▶ Caddy (owns 80/443, auto-TLS per hostname)
                     ├─ shop.…        ──▶ frontend   (nginx, static SPA)
                     ├─ auth.…        ──▶ keycloak:8080
                     └─ *-api.…       ──▶ {catalog,order,payment}:8080   (plain HTTP inside)
order ──gRPC h2c──▶ catalog:8081          (dedicated cleartext HTTP/2 Kestrel endpoint)
services ──AMQP──▶ rabbitmq   ──▶ consumers (durable queues, Wolverine inbox/outbox)
```

TLS terminates at Caddy; traffic inside the Docker network is plain HTTP (see the honesty
table). JWTs are validated against the public authority (`auth.emeraldleaf.dev`), so the
token issuer matches in the browser and in every service.

## How code reaches the box (the deploy pipeline)

```
merge to main
  └─▶ GitHub Actions (publish-images.yml): builds 6 images → pushes ghcr.io/…:latest
        └─▶ the box polls: nextaurora-deploy.timer (every 3 min)
              └─▶ deploy.sh: docker compose pull && up -d   (recreates only changed containers)
```

**Pull, not push — deliberately.** The box's SSH allowlist admits only known IPs; GitHub's
runners come from ever-changing ranges, so a push-style SSH deploy job times out (tried,
removed). The timer inverts the direction: nothing needs inbound access, and *merging to
main is deploying* (~10 min end to end: image build ≈ 7, poll ≤ 3).

## What `DemoMode=true` gates (and what it doesn't)

One explicit flag, set in three places — the deployed compose file, [fly.toml](../fly.toml),
and (payment-service only) the local Aspire AppHost — never a default. Everything it changes is a *posture* switch, not a feature flag:

| With `DemoMode=true` | Why it's a demo posture, not the production one |
|---|---|
| EF migrations run at startup (all four DB services) | Production runs migrations as a reviewed deploy step; single replica + throwaway data makes in-process migrate the right trade here |
| OpenAPI + Scalar exposed (`/scalar/v1`) | Public API docs are part of the demo surface; production APIs don't ship their explorer |
| `UseHttpsRedirection` skipped | TLS terminates at Caddy; redirecting behind the proxy would loop |
| Kill switch mapped (PaymentService `/api/v1/demo/*`) | Without the flag the routes don't exist (404) — a non-demo deployment has no listener controls at all |

Orthogonal flags: `Turnstile:Enabled` (bot gate on `POST /orders` + kill switch — fail-closed,
off in dev/tests) and `Wolverine:AutoProvision`. Auth, authorization, rate limits, validation,
the outbox, and idempotency are **not** relaxed by DemoMode — the security posture is the
production one; only the operational posture is demo-grade.

## Demo-grade by choice — what production would do differently

The system's *architecture* is production-shaped; these **operational** choices are not, and
each is deliberate:

| Demo choice | Production shape |
|---|---|
| Public credentials (`buyer1`/`buyer1`) + password grant working for curl | Real registration; password grant disabled (the SPA already uses auth-code + PKCE) |
| One shared box, one replica of everything | Replicas + a scheduler; the plan's D4 notes what changes first (in-memory rate limiters → Redis-backed, HybridCache backplane) |
| Secrets in a root-only `.env` on the box | A secret manager (Vault / cloud KMS); per-service DB users instead of `sa` / one Postgres role |
| SQL Server Express, capped at 1.3 GB | Licensed edition sized to the workload, not to a shared box's RAM budget |
| No database backups | Point-in-time recovery; the demo's data is deliberately throwaway |
| Plain HTTP + AMQP inside the Docker network | mTLS or a service mesh for east-west traffic |
| DataProtection keys ephemeral per container | Persisted, shared key ring (no cookies/antiforgery in the APIs, so nothing breaks — but it's not the production default) |
| Stripe gateway is a stub | A real PSP integration behind the same `IPaymentGateway` port |
| A kill switch that pauses a consumer from the UI | Chaos tooling in staging, never a public button |
| Migrations at startup | Reviewed migration step in the pipeline (see the DemoMode table) |

If a claim above ever stops matching the code, that's a doc bug — this file follows the
repo's [doc-and-diagram discipline](../CLAUDE.md#debugging-discipline).

## Observing it

- **Wolverine's inbox/outbox tables** (`wolverine.*` schemas) show envelopes moving; the
  first night in production the durable inbox rejected a genuine RabbitMQ redelivery —
  recorded in [performance-and-data-correctness.md "Container memory"](performance-and-data-correctness.md#container-memory-workstation-gc-and-caps-from-measurement).
- **Container logs** on the box: `docker compose -f /root/nextaurora/docker-compose.services.yml logs -f order`.
- **Deploy history:** `journalctl -u nextaurora-deploy.service` — one line per roll.
- Memory/caps: `docker stats` — every container is hard-capped (shared box; see the
  workstation-GC rule in CLAUDE.md "Performance Rules").

## Incidents

Kept here (not STATUS.md) because the lessons are durable.

### 2026-08-28 — all NextAurora hostnames lost TLS for ~12 h

**Symptom:** `shop`, `auth`, and the three `*-api` hosts failed the TLS handshake (`tlsv1 alert
internal error` = Caddy had no certificate for the SNI); `riparian.emeraldleaf.dev` on the same
box was fine. Port 80 still answered with a 308, which looks healthy but is only Caddy's generic
redirect.

**Cause:** Caddy is *owned by the Riparian deployment* (`/root/riparian-rag-harness/deploy/`);
NextAurora contributes only `caddy/nextaurora.caddy`, which Riparian's `Caddyfile.full` imports
and its compose file mounts. Those two lines had been added as **live-only edits** on 2026-08-27
and never committed to the Riparian repo. A Riparian redeploy re-synced the files without them
and recreated Caddy with only the Riparian site. Nothing alerted; a link check on the portfolio
site found it 12 hours later.

**Fix:** commit the import + mount in the Riparian repo (`fix/caddy-nextaurora-import`), re-sync,
`docker compose -f docker-compose.full.yml up -d caddy`. Caddy re-listed all six domains and
reused the certificates already in the `deploy_caddy_data` volume — no re-issuance, no rate-limit
exposure.

**Prevention:** (1) `.github/workflows/uptime.yml` probes every public hostname every 30 minutes
and opens/updates an `uptime` issue on failure. (2) Rule: **anything NextAurora needs from the
shared Caddy must be committed in the Riparian repo, never edited live** — the box's copy is
overwritten by every Riparian push. (3) After any Riparian redeploy, the Caddy log must list the
NextAurora domains under `enabling automatic TLS certificate management`.
