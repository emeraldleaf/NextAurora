# Deployment Story — Getting CatalogService Live on Fly.io

A step-by-step narrative of what we actually did to deploy [CatalogService.Api](../CatalogService/CatalogService.Api/) to a public URL, including the dead ends and why we ended up where we did. Useful for walking someone through the deployment story or refreshing your own memory.

For the reusable checklist (do this from scratch), see [demo-deployment.md](demo-deployment.md). This doc is the *story*; that doc is the *recipe*.

---

## Goal

Get a working public URL serving `CatalogService.Api` with the Scalar API documentation reachable, on as little budget and complexity as possible — without breaking any of the existing local development paths (Aspire, integration tests, future production deploy).

## What "done" looks like

- `https://catalog-api-demo.fly.dev/health` → `Healthy`
- `https://catalog-api-demo.fly.dev/api/v1/products` → JSON product list (empty, since we haven't seeded data)
- `https://catalog-api-demo.fly.dev/openapi/v1.json` → valid OpenAPI 3.1 spec
- `https://catalog-api-demo.fly.dev/scalar/v1` → interactive Scalar UI
- Local `dotnet run --project NextAurora.AppHost` still behaves identically to before

## High-level shape we landed on

```
  Your laptop                 Fly.io (Los Angeles region)
  ───────────                 ──────────────────────────────────
  fly deploy        ─►  Fly remote builder
                          │
                          │ docker build -f Dockerfile.catalog
                          ▼
                        Fly registry  ─►  Fly Machine running catalog-api-demo
                                              │  ┌─ env: DemoMode=true (from fly.toml)
                                              │  └─ secret: CATALOG_DB_CONNECTION_STRING
                                              ▼
                                          Fly Postgres (catalog-demo-db)
                                              (1x shared CPU, 1GB volume, suspends when idle)
```

---

## Step 1 — Make the code deploy-aware (`DemoMode` flag)

**Problem**: `CatalogService.Api` was wired for two environments — local development (where Scalar/OpenAPI are exposed) and a hypothetical production (where they're hidden because OpenAPI specs are reconnaissance gold). For the demo we needed a *third* mode: Production-environment behavior PLUS Scalar visibility, because the whole point is showing the API documentation.

**Solution**: a `DemoMode` configuration flag in [Program.cs](../CatalogService/CatalogService.Api/Program.cs). When set, it:
1. Exposes `/openapi/v1.json`, `/openapi/v1.yaml`, `/scalar/v1` even outside Development
2. Skips `UseHttpsRedirection()` (PaaS hosts terminate TLS at the edge — would cause redirect loops)
3. Runs EF Core migrations on startup (so we don't need a separate "deploy migrations" step)

**Why this is safe**: the flag defaults to `false`. When absent — every existing code path (local Aspire, tests, real production if we ever ship one) behaves byte-for-byte identically to before. Backward-compat verified in detail in [demo-deployment.md "Backward compatibility"](demo-deployment.md#backward-compatibility--what-this-does-not-change).

## Step 2 — Make Redis optional

`CatalogService.Infrastructure` registers Redis via HybridCache's L2 tier. For a single-replica demo we don't want to pay for managed Redis. The registration is now conditional: if no `cache` connection string is configured, Redis isn't registered, and HybridCache gracefully degrades to L1-only (in-process MemoryCache). When run via Aspire locally, Redis IS registered because `WithReference(cache)` provides the connection string — so local dev is unchanged.

## Step 3 — Containerize

[Dockerfile.catalog](../Dockerfile.catalog) at the repo root, multi-stage:
- **Build stage**: `mcr.microsoft.com/dotnet/sdk:10.0` → restore → publish
- **Runtime stage**: `mcr.microsoft.com/dotnet/aspnet:10.0` → non-root `app` user → port 8080

[.dockerignore](../.dockerignore) excludes other services, tests, docs, build artifacts — keeps the build context small (~50MB) and stops accidentally shipping `appsettings.Development.json`.

Important detail discovered late: **the build needed `.editorconfig`** in the build context. Without it, the Roslyn analyzers fall back to their stricter defaults and treat CA1062, CA2007, CA1724, MA0004 as errors under our `TreatWarningsAsErrors=true`. Locally these are suppressed by the `.editorconfig` rules at the repo root.

## Step 4 — Pick a hosting provider (after a dead end)

**Original plan**: AWS App Runner + RDS Postgres free tier. Built scaffolding (Dockerfile, GitHub Actions workflow with OIDC → ECR push, AWS click-path docs).

**Why we pivoted**: AWS billing rejected the payment method on first-card-attempt with no obvious recourse. Pivoted to **Fly.io** as the primary path. Same Dockerfile, same `DemoMode` flag, same architecture — only the orchestration layer changed. The AWS scaffolding is still in the repo as an alternative at [.github/workflows/deploy-catalog-demo.yml](../.github/workflows/deploy-catalog-demo.yml).

**Why Fly specifically** (over Railway, Render, etc.):
- Stripe-based billing (more permissive than AWS for unusual cards)
- Single-replica auto-stop-on-idle is a config flag, not a separate product
- Postgres is a one-liner (`fly postgres create`)
- The same Dockerfile works without modification

## Step 5 — Cap the spend ($25 prepaid credits)

Fly doesn't offer hard spending caps in the dashboard anymore — only soft email alerts. To bound the demo cost with certainty:

1. Bought **$25 in Fly credits** on the billing page
2. **Did NOT add a credit card** under "Payment Method"

This makes credits the only payment source. When they run out, Fly suspends services instead of charging more. Realistic burn is ~$5/mo (app auto-stops when idle, Postgres ~$2-3/mo, account minimum $5/mo) — so $25 covers ~5 months of runtime, far more than the demo needs.

## Step 6 — Provision Fly resources

```bash
brew install flyctl
fly auth signup                # browser, create account
fly apps create catalog-api-demo
fly postgres create            # interactive — chose name catalog-demo-db, region lax,
                               # Development tier (1x shared CPU, 256MB, 1GB disk),
                               # scale-to-zero enabled
```

Region: `lax` (Los Angeles) because Fly defaulted to it and Postgres + app must be in the same region for `.flycast` low-latency internal networking. Our [fly.toml](../fly.toml) was originally set to `iad` (Ashburn) and we updated it to `lax` to match.

Postgres provisioning prints the connection details once — **password is unrecoverable after that**, so save it immediately.

## Step 7 — Bridge the secret name (Fly's stricter naming)

**Problem**: Fly's secret names only allow `[A-Z0-9_]` — hyphens are rejected. But our app reads `GetConnectionString("catalog-db")` (kebab-case, set by Aspire's `WithReference()` convention). The corresponding env var name would be `ConnectionStrings__catalog-db`, which Fly bounces.

**Solution**: a tiny adapter in [Program.cs](../CatalogService/CatalogService.Api/Program.cs) that, only when `DemoMode=true`, reads from a Fly-compatible secret name (`CATALOG_DB_CONNECTION_STRING`) and copies it into the `ConnectionStrings:catalog-db` slot the Infrastructure layer reads from. 5 lines, fully gated behind the demo flag, doesn't touch Aspire wiring.

Then set the secret:

```bash
fly secrets set "CATALOG_DB_CONNECTION_STRING=Host=catalog-demo-db.flycast;Port=5432;Database=catalog;Username=postgres;Password=<from step 6>;SSL Mode=Disable" -a catalog-api-demo
```

**`SSL Mode=Disable` is required** — Fly's legacy unmanaged Postgres on the `.flycast` internal network doesn't speak SSL (the network is already a private encrypted overlay). Npgsql's default `SSL Mode=Prefer` tries TLS first and crashes hard with `Exception while performing SSL handshake / Received an unexpected EOF` instead of falling back to plain. The first deploy failed this way; the second succeeded after appending `SSL Mode=Disable`. Safe here because flycast is private-network-only; production-grade Postgres over public internet absolutely needs SSL on.

Database name `catalog` doesn't exist yet — EF Core's `Migrate()` creates it on first run because the `postgres` user has `CREATEDB` permission by default in a fresh Fly Postgres.

## Step 8 — Deploy

```bash
fly deploy --remote-only
```

`--remote-only` builds the Docker image on Fly's remote builder VM instead of your laptop — sidesteps any local Docker issues (ours was broken from an earlier disk-full event). Builder tarballs the working directory (uncommitted changes included — git state doesn't matter), uploads, runs the multi-stage Dockerfile, pushes to Fly's per-app registry, creates a Machine running the new image.

First build: ~5 min (NuGet restore + SDK image pull are the slow parts). Subsequent builds reuse the layer cache and run in ~1-2 min.

On first boot, the Machine:
1. Reads env vars from fly.toml `[env]` section (`ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS=http://+:8080`, `DemoMode=true`)
2. Reads secrets injected by Fly (`CATALOG_DB_CONNECTION_STRING`)
3. `DemoMode` bridge fires, populating `ConnectionStrings:catalog-db`
4. EF Core `Migrate()` runs (see Step 9 below for what it actually does)
5. Web server binds on port 8080
6. Fly health-checker hits `/health` — once 200, marks the deploy successful

## Step 9 — EF Core migrations run on startup (a DemoMode exception)

This is the one decision in the demo deploy that deliberately *violates* a production rule. Worth understanding clearly because it's a question someone will ask.

### What actually happens

[Program.cs](../CatalogService/CatalogService.Api/Program.cs) ends its startup with:

```csharp
if (app.Environment.IsDevelopment() || isDemoMode)
{
    await app.Services.MigrateDatabaseAsync<CatalogDbContext>();
}
```

`MigrateDatabaseAsync<T>()` (in [NextAurora.ServiceDefaults](../NextAurora.ServiceDefaults/)) wraps EF Core's `DbContext.Database.MigrateAsync()`. On first boot against the empty Fly Postgres:

1. Tries to connect to `Database=catalog` — connection fails because the database doesn't exist
2. Falls back to the maintenance database (`postgres`) and issues `CREATE DATABASE catalog` — works because Fly Postgres's default `postgres` user is a superuser with `CREATEDB`
3. Connects to the newly-created `catalog` database
4. Checks the `__EFMigrationsHistory` table (empty on first run)
5. Applies all unapplied migrations in order — currently just `20260503040949_InitialCreate`
6. Records the migration ID + EF Core version in `__EFMigrationsHistory` so the next boot knows it's done

After this, the schema is in place: `Products`, `Categories`, `ProductStock` tables, indexes, foreign keys, and the `xmin`-based concurrency token configuration on every aggregate root. Zero data — we don't seed.

On every *subsequent* boot, `Migrate()` reads `__EFMigrationsHistory`, sees the migration is already applied, and returns in milliseconds. Idempotent by design.

### Why this is "production-incorrect"

[CLAUDE.md "Performance Rules"](../CLAUDE.md#performance-rules) says: *"Migrations are immutable once applied"*, and the [migrations doc](ef-core.md#6-migrations) is explicit that production should run migrations as a **separate deploy step**, not in-process at app startup. Two reasons:

1. **Multi-replica race**. In production with N replicas behind a load balancer, all N machines start simultaneously, all N call `Migrate()`, all N race to acquire the same advisory lock and apply the same migration. EF Core's `Migrate()` IS safe under this race (it uses a Postgres advisory lock), but the *failed* replicas crashloop or hang waiting on the lock, and the deploy looks broken even when the schema is fine.
2. **Schema changes are riskier than image deploys**. You want them gated behind explicit human review, not "well, the new image happened to also include a destructive migration." Separating the migration step lets you `dotnet ef migrations script` the SQL, eyeball it, run it via CI with idempotent flags, and only THEN roll out the new image.

### Why it's OK in DemoMode

The two production hazards don't apply:

1. **Single replica** — our fly.toml sets `min_machines_running = 0` and we never scale up. There's no race because there's no second replica.
2. **Demo data is throwaway** — if a future migration destroys the catalog, we just delete the Postgres and re-deploy. Real production data deserves a more careful workflow; demo data doesn't.

Run-on-startup also saves us from having to wire up a separate "migration job" step in CI before the deploy job — for a demo, the simpler one-step path is the right call.

### What the initial migration creates

The schema EF Core creates on first boot:

| Table | Notable columns |
|---|---|
| `Products` | `Id` (PK), `Name`, `Description`, `Price`, `Currency`, `CategoryId` (FK), `xmin` (concurrency token, system column) |
| `Categories` | `Id` (PK), `Name`, `Description`, `xmin` |
| `ProductStock` | `ProductId` (PK + FK), `Quantity`, `Reserved`, `xmin` |
| `__EFMigrationsHistory` | EF Core's own metadata: `MigrationId`, `ProductVersion` |

The `xmin` system column is Postgres-specific — it's the transaction ID of the row's last write, automatically updated by Postgres on every UPDATE. EF Core uses it as an optimistic concurrency token by including it in the `WHERE` clause of every UPDATE. If two writes race, the second one's `WHERE xmin = N` matches zero rows, EF throws `DbUpdateConcurrencyException`, and our [GlobalExceptionHandler](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs) returns HTTP 409. Full rationale: [ef-core.md "Concurrency tokens"](ef-core.md#5-concurrency-tokens--xmin-vs-rowversion).

### How future migrations would deploy

If we change a domain entity later (e.g. add a `Sku` field to `Product`):

```bash
dotnet ef migrations add AddProductSku \
  --project CatalogService/CatalogService.Infrastructure \
  --startup-project CatalogService/CatalogService.Api
```

This generates a new `.cs` file in `Migrations/`. Commit it. Next `fly deploy --remote-only` ships the new code + new migration, the Machine reboots, `Migrate()` notices `AddProductSku` is unapplied, runs the `ALTER TABLE` it contains, and the new boot is serving with the new schema. Zero downtime if the change is backward-compatible (additive columns, new indexes, new tables). Forward-incompatible changes (drop column, rename, NOT NULL on existing column) need the multi-step plan described in [ef-core.md "The immutable-once-applied rule"](ef-core.md#67-the-immutable-once-applied-rule).

## Step 9.5 — Seed data via EF Core `HasData` (and wire up CI/CD)

After the first deploy worked, the catalog was empty (`GET /api/v1/products` returned `[]`). Two ways to fix that: ad-hoc `INSERT` over `fly postgres connect`, or a proper EF Core seed migration. We did the migration so the deploy story also demonstrates CI/CD and the canonical EF Core seeding pattern.

### Adding the seed

In [CatalogDbContext.cs](../CatalogService/CatalogService.Infrastructure/Data/CatalogDbContext.cs), `OnModelCreating` calls a private `SeedDemoData` method that uses `modelBuilder.Entity<T>().HasData(...)` to declaratively register 3 categories and 7 products. Fixed GUIDs and a fixed `CreatedAt` (not `Guid.NewGuid()` / `DateTime.UtcNow`) so the generated migration is **deterministic** — re-running the model snapshot wouldn't emit a diff.

`HasData` writes via reflection, which **bypasses** the entity's factory method (`Product.Create`) and private setters. That's the right trade for curated design-time data — validation is unnecessary because we control the values. We still set `IsAvailable` explicitly to match the `StockQuantity > 0` invariant the factory would have enforced.

Then generate the migration:

```bash
dotnet ef migrations add SeedDemoCatalog \
  --project CatalogService/CatalogService.Infrastructure \
  --startup-project CatalogService/CatalogService.Api \
  --context CatalogDbContext
```

EF Core produced two files:
- `Migrations/20260518133000_SeedDemoCatalog.cs` — `Up()` with `InsertData` calls (categories first per FK ordering), `Down()` with matching `DeleteData` calls
- `Migrations/20260518133000_SeedDemoCatalog.Designer.cs` — frozen model snapshot at time of migration

Plus EF updated the live model snapshot (`CatalogDbContextModelSnapshot.cs`) to include the seed data.

### Wiring CI/CD (Fly + GitHub Actions)

Up to this point every deploy was a local `fly deploy --remote-only`. To exercise CI/CD properly:

```bash
fly tokens create deploy -a catalog-api-demo
# → prints "FlyV1 fm2_lJ..." token (app-scoped, not org-scoped)
```

Save the token to GitHub as a **Repository secret** at `Settings → Secrets and variables → Actions`:
- Name: `FLY_API_TOKEN`
- Value: the token

The [.github/workflows/deploy-catalog-demo-fly.yml](../.github/workflows/deploy-catalog-demo-fly.yml) workflow reads it via `${{ secrets.FLY_API_TOKEN }}` and runs `flyctl deploy --remote-only --config fly.toml`. The workflow is `workflow_dispatch` only (manual trigger) — by design, demo deploys shouldn't fire on every push.

Trigger from GitHub: **Actions → DEPLOY_CATALOG_DEMO_FLY → Run workflow → Run workflow**.

### What happens end-to-end

1. GitHub Actions runner spins up
2. Checks out `main` from `origin`
3. Installs `flyctl` via `superfly/flyctl-actions/setup-flyctl@master`
4. `flyctl deploy --remote-only` — uploads source to Fly's remote builder, builds image, pushes to Fly registry, creates a new Fly Machine
5. New Machine boots: DemoMode flag fires → ForwardedHeaders middleware → DemoMode secret bridge → EF Core `Migrate()` — finds `InitialCreate` already applied, applies `SeedDemoCatalog` (the new one), inserts 3 categories + 7 products
6. Fly's health checker hits `/health` → 200 → old Machine is destroyed → new Machine is the live one

### Verifying

```bash
curl -sS https://catalog-api-demo.fly.dev/api/v1/products | jq
```

Returns 7 products: NextAurora Laptop, Wireless Headphones, USB-C Hub, Standing Desk, Ceramic Pour-Over Kettle, *Designing Data-Intensive Applications*, *The Pragmatic Programmer*. Includes one with `stockQuantity: 0` (USB-C Hub) so the `IsAvailable` invariant — `IsAvailable === stockQuantity > 0` — is also visible in the response.

### Future migration deploys

If we change the seed data (or add a real schema migration), the next CI run does the right thing:
1. `dotnet ef migrations add MyChange` → commit → push to `main`
2. Trigger **DEPLOY_CATALOG_DEMO_FLY** workflow
3. New Machine boots, `Migrate()` checks `__EFMigrationsHistory`, sees `MyChange` is unapplied, runs only that one's `Up()`
4. Old Machine destroyed, new one is serving

Zero downtime if the change is backward-compatible (additive columns, new indexes, INSERT-only). Forward-incompatible changes (drop column, rename, NOT NULL on existing column) need the multi-step plan in [ef-core.md "The immutable-once-applied rule"](ef-core.md#67-the-immutable-once-applied-rule).

## Step 10 — Verify

Live at https://catalog-api-demo.fly.dev. All five endpoints verified:

| Endpoint | Status | Notes |
|---|---|---|
| `GET /health` | 200 (`Healthy`) | Cold-start latency ~9s after idle, sub-second once warm |
| `GET /api/v1/products` | 200, 7 products | After the `SeedDemoCatalog` migration shipped via CI/CD (see Step 9.5). Pre-seed it was `[]`. |
| `GET /openapi/v1.json` | 200, valid OpenAPI 3.1.1 | |
| `GET /openapi/v1.yaml` | 200, valid YAML | |
| `GET /scalar/v1` | 200, interactive UI | Open this in a browser for the demo |

Reproduce locally:

```bash
URL=https://catalog-api-demo.fly.dev
curl -sS $URL/health
curl -sS $URL/api/v1/products
open $URL/scalar/v1
```

## What this demonstrates

Useful when walking someone through the deployment, or as a refresher when you come back to this later:

1. **"`DemoMode` flag is a deliberate security relaxation."** Production posture (Scalar hidden, OpenAPI hidden, HTTPS redirect on, migrations as separate step) is unchanged because the flag defaults to false. The whole adaptation is gated behind one bool.

2. **"Same Dockerfile deploys to Fly OR AWS — that's the abstraction containers buy you."** We have scaffolding for both providers in the repo. Switching took 15 min when AWS billing didn't work.

3. **"HybridCache degrades to L1-only when no `cache` connection string is set."** Demonstrates graceful capability degradation — production adds managed Redis, demo runs without it, same code path.

4. **"Single-service demo, no saga choreography."** Cross-service Order → Payment → Shipping flows need ≥2 services + a message bus, which doesn't fit a free-tier budget. The saga story is told via the architecture doc and integration tests, not via the deployed environment.

5. **"$25 prepaid credit instead of a saved card — hard spending cap."** Fly's dashboard doesn't offer hard caps, only soft alerts. Prepaying without a card on file forces Fly to suspend services rather than charge a card when credits run out.

6. **"DemoMode bridge for Fly's secret-name validation."** Fly rejects hyphens in secret names; Aspire convention uses kebab-case (`catalog-db`). A 5-line adapter in Program.cs bridges the two, gated behind DemoMode so production wiring is untouched.

7. **"`.editorconfig` had to be explicitly copied into the Docker build context."** Without it, Roslyn analyzers fall back to defaults and TreatWarningsAsErrors trips on CA1062/CA2007/CA1724/MA0004. Subtle: locally it Just Works because the file is in the repo root; in Docker the build context only contains what `COPY` brings in.

8. **"EF Core migrations run on startup — deliberate violation of a production rule, scoped to DemoMode."** Production would run `dotnet ef database update` as a separate CI step before the deploy job, because (a) multi-replica races make in-process Migrate() noisy, and (b) schema changes deserve human review. Demo runs one replica and uses throwaway data, so the simpler one-step path is the right trade. The flag gates it: production posture (where the rule applies) is unchanged. Full mechanics + the migrations-as-code-vs-data discussion: [Step 9 above](#step-9--ef-core-migrations-run-on-startup-a-demomode-exception) and [ef-core.md §6](ef-core.md#6-migrations).

9. **"Seed data lives in the model config via `HasData`, not in a separate SQL script."** Canonical EF Core seeding pattern — declarative, deterministic (fixed GUIDs / fixed `CreatedAt`), and `dotnet ef migrations add` generates the `InsertData` calls automatically. Future seed changes go through migrations too, so the history is reproducible and reversible (`Down()` is auto-generated). Bypasses the entity's factory method via reflection, which is the right trade for curated design-time data. Full mechanics: [Step 9.5 above](#step-95--seed-data-via-ef-core-hasdata-and-wire-up-cicd).

10. **"End-to-end CI/CD demonstrated."** Code change → `git push` → manual workflow trigger (`workflow_dispatch`, not push-triggered, because demo deploys shouldn't fire on every commit) → GitHub Actions runner → Fly's remote builder builds Dockerfile → Fly Machine boots → EF Core picks up the new seed migration → live data on a public URL. Single token (`FLY_API_TOKEN`, app-scoped, generated with `fly tokens create deploy -a catalog-api-demo`) authorizes the whole thing.

## Gotchas hit along the way (in case you redo this)

In rough order:

1. **AWS billing rejected the card** on first try with no obvious recourse. → Pivoted to Fly.
2. **Local Docker daemon was corrupted** from an earlier disk-full event. → `--remote-only` builds on Fly's builder, sidestepping local Docker entirely.
3. **Fly removed dashboard-level spending caps**; only soft alerts remain. → Bought $25 prepaid credits and didn't save a card. When credits hit $0, Fly suspends instead of charging. Effective hard cap.
4. **Fly's `fly postgres create` warns it's "unmanaged"** and pushes Managed Postgres ($15+/mo). → Legacy unmanaged is fine for throwaway demo data; ignored the nudge.
5. **Fly secret names reject hyphens** (`[A-Z0-9_]` only). → Added a `DemoMode`-only bridge in [Program.cs](../CatalogService/CatalogService.Api/Program.cs) that copies `CATALOG_DB_CONNECTION_STRING` into `ConnectionStrings:catalog-db`. Aspire wiring untouched.
6. **Docker build failed: analyzer errors (CA1062/CA2007/CA1724/MA0004) under `TreatWarningsAsErrors=true`.** → The `.editorconfig` at the repo root suppresses these; wasn't being copied into the build context. Added to the COPY line in [Dockerfile.catalog](../Dockerfile.catalog).
7. **First deploy crashed: `Exception while performing SSL handshake / Received an unexpected EOF`** on the EF Core migration's first Postgres connection. → Fly's legacy unmanaged Postgres on `.flycast` doesn't speak SSL. Npgsql's default `SSL Mode=Prefer` crashes hard instead of falling back to plain. Fix: append `SSL Mode=Disable` to the connection string. Flycast is already a private encrypted network, so disabling Postgres-layer SSL is safe inside that perimeter.
8. **Health-check grace period was too short** for first boot (20s default vs ~30-60s for migration + Postgres connect). → Bumped to 120s in fly.toml. Subsequent boots are fast because `Migrate()` finds the migration already applied and returns in ms.

## Tear-down

```bash
fly apps destroy catalog-api-demo
fly apps destroy catalog-demo-db
```

Stops billing immediately. The $25 in credits stays in your account for future use.
