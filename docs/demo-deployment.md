# Demo Deployment — CatalogService

One-time setup to get [CatalogService.Api](../CatalogService/CatalogService.Api/) running on a public URL with the Scalar UI exposed for a public demo.

For a narrative walkthrough of the actual deploy session (what we did, why we made each call, dead ends along the way), see [demo-deployment-story.md](demo-deployment-story.md). This doc is the *recipe*; that one is the *story*.

Two paths are documented below — pick one:

| Path | Setup time | Cost | When to pick |
|---|---|---|---|
| **[Fly.io](#flyio-path-recommended)** (primary) | ~15 min | ~$0-$5/mo | Fastest, fewest moving parts, accepts almost any payment method |
| **[AWS App Runner](#aws-app-runner-path-alternative)** | ~30 min | ~$5/mo (RDS free tier first 12 mo) | If you want the AWS knowledge surface on your walkthrough, and AWS billing accepts your card |

Both deploy the same artifact (`Dockerfile.catalog`) with `DemoMode=true`, talk to a managed Postgres, and produce a public HTTPS URL like `https://xxx.fly.dev/scalar/v1` or `https://xxx.awsapprunner.com/scalar/v1`.

> **Security note**: both paths deploy with `DemoMode=true`, which surfaces OpenAPI + Scalar in a non-Development environment. That's a deliberate relaxation for the demo — production keeps OpenAPI hidden because the spec is reconnaissance gold. See [project-decisions.md "Why Scalar over Swagger UI"](project-decisions.md#why-scalar-over-swagger-ui).

## Backward compatibility — what this does NOT change

The demo scaffolding is fully additive. Local Aspire development, the test suite, and a hypothetical future production deploy all behave identically to before when `DemoMode` is not set:

| Surface | When `DemoMode` is absent | Why |
|---|---|---|
| `dotnet run --project NextAurora.AppHost` (local Aspire) | Unchanged | All three `DemoMode` branches in [Program.cs](../CatalogService/CatalogService.Api/Program.cs) short-circuit: `IsDevelopment() \|\| false` → `IsDevelopment()`. |
| Redis registration | Unchanged | Aspire's `WithReference(cache)` sets `ConnectionStrings__cache`, so the new conditional still registers `AddStackExchangeRedisCache`. Skipping only triggers when no `cache` conn string is wired at all. |
| `dotnet build` | Unchanged | Zero new warnings under `TreatWarningsAsErrors`. |
| Integration tests | Unchanged | Testcontainers provides Redis via the same `ConnectionStrings__cache` path. |
| Existing CI workflows ([ci.yml](../.github/workflows/ci.yml), [codeql.yml](../.github/workflows/codeql.yml)) | Unchanged | New workflows are `workflow_dispatch` only — never fire on push or PR. |
| Production posture (if/when we deploy real prod) | Unchanged | `DemoMode` defaults to `false`. OpenAPI + Scalar stay hidden. HTTPS redirection stays on. Migrate-on-startup stays off. |

**Watch-out**: if you ever export `ConnectionStrings__catalog-db=<remote-endpoint>` in your local shell, a bare `dotnet run --project CatalogService/CatalogService.Api` would try to talk to the remote DB. This is self-inflicted-only — `dotnet run --project NextAurora.AppHost` overrides connection strings before child processes inherit them, so Aspire-driven local runs are immune.

The [Dockerfile.catalog](../Dockerfile.catalog) and [.dockerignore](../.dockerignore) at the repo root are pure opt-in — Aspire runs the .NET services as `dotnet` processes (only infra deps like Postgres/SQL/Redis/Keycloak/ASB-emulator run in containers), so nothing in the local workflow invokes `docker build`.

## What gets deployed (either path)

- **CatalogService.Api** as a single replica, scale-to-zero when idle
- **Managed Postgres** for product/stock data (Fly Postgres or AWS RDS depending on path)
- **No Redis** — HybridCache degrades to L1-only (in-process MemoryCache). Real prod would add a managed Redis for L2.
- **No Service Bus / no other services** — single-service demo. Cross-service choreography (Order → Payment → Shipping saga) doesn't fit a free-tier budget; flag this as a "would need ASB + ≥2 services" caveat when walking through the deployment.

---

# Fly.io path (recommended)

```
You ──[fly deploy]──> Fly remote builder
                            │
                            │ builds Dockerfile.catalog
                            ▼
                       Fly registry
                            │
                            │ rolls Machine to new revision
                            ▼
                    Fly Machine (catalog-api-demo)
                            │  ┌─ env: DemoMode=true
                            │  └─ secret: ConnectionStrings__catalog-db
                            ▼
                    Fly Postgres (catalog-demo-db)
```

## 1. Install flyctl + sign up

```bash
brew install flyctl
fly auth signup       # opens browser → create account, add payment method
                      # (or `fly auth login` if you already have one)
```

Fly requires a payment method but the hobby tier costs ~$0 if the app sleeps when idle (which our `auto_stop_machines = "stop"` config enables).

## 2. Create the app (without deploying)

From the repo root:

```bash
fly launch --copy-config --no-deploy --name catalog-api-demo --region iad
```

- `--copy-config` uses the existing [fly.toml](../fly.toml) (don't let it overwrite)
- `--no-deploy` skips the first deploy (we don't have a Postgres yet)
- `--region iad` picks Ashburn, VA. Other options: `ord` (Chicago), `lax` (LA), `lhr` (London), `fra` (Frankfurt)

If it asks "Would you like to copy its configuration to the new app?" → **yes**.
If it asks about a Postgres or Redis cluster → **no** (we create the Postgres separately).

## 3. Provision Postgres

```bash
fly postgres create \
  --name catalog-demo-db \
  --region iad \
  --vm-size shared-cpu-1x \
  --volume-size 1 \
  --initial-cluster-size 1
```

Confirm the prompts. When done, Fly prints connection info — **copy the password immediately**; you can't retrieve it later.

## 4. Wire the connection string as a secret

Fly Postgres exposes itself on Fly's internal network. The hostname format is `<pg-app-name>.flycast` reachable only from other Fly apps in the same org.

```bash
fly secrets set \
  "ConnectionStrings__catalog-db=Host=catalog-demo-db.flycast;Port=5432;Database=catalog;Username=postgres;Password=<paste-password-here>" \
  -a catalog-api-demo
```

The database `catalog` is auto-created by EF Core migrations on first boot (because `DemoMode=true` runs `MigrateDatabaseAsync<CatalogDbContext>()` at startup). Note: the default Fly Postgres user is `postgres`, not the role we'd use in real prod.

## 5. First deploy

```bash
fly deploy --remote-only -a catalog-api-demo --config fly.toml
```

`--remote-only` builds the Docker image on Fly's builder instead of locally — sidesteps any local Docker issues. Takes ~3-5 min for the first build (subsequent builds reuse layer cache).

When done, Fly prints the public URL: `https://catalog-api-demo.fly.dev`.

## 6. Verify

```bash
URL=https://catalog-api-demo.fly.dev
curl -sS $URL/health                  # → "Healthy"
curl -sS $URL/api/v1/products         # → JSON product list (empty initially)
open $URL/scalar/v1                   # → Scalar interactive API UI
```

If `/scalar/v1` returns 404, the `DemoMode` env var didn't get baked in — check `fly config show -a catalog-api-demo`.

## 7. (Optional) Wire up the GitHub Actions workflow

For one-click subsequent deploys instead of needing `flyctl` locally:

```bash
fly tokens create deploy -a catalog-api-demo   # prints a token starting with FlyV1
```

GitHub: **Settings → Secrets and variables → Actions → New repository secret**
- Name: `FLY_API_TOKEN`
- Value: the token from above

Future deploys: **Actions → DEPLOY_CATALOG_DEMO_FLY → Run workflow**. See [.github/workflows/deploy-catalog-demo-fly.yml](../.github/workflows/deploy-catalog-demo-fly.yml).

## Tear-down (Fly path)

```bash
fly apps destroy catalog-api-demo
fly apps destroy catalog-demo-db
```

The `FLY_API_TOKEN` secret in GitHub can stay (it's app-scoped; destroying the app invalidates it).

---

# AWS App Runner path (alternative)

Use this path if you want AWS specifically — slower setup, ~$5/mo. Requires a card AWS will accept.

## What gets deployed (AWS specifics)

- **CatalogService.Api** as an App Runner service
- **RDS Postgres** (`db.t4g.micro`, free tier 12 mo)

## Architecture

```
GitHub Actions ──[OIDC]──> AWS IAM Role
        │
        │ docker build/push
        ▼
   Amazon ECR (catalog-api:latest)
        │
        │ auto-deploy on push
        ▼
   AWS App Runner (catalog-api-demo)
        │  ┌─ env: DemoMode=true
        │  └─ env: ConnectionStrings__catalog-db=...
        ▼
   AWS RDS Postgres (free tier)
```

## 1. RDS Postgres (free tier)

Console: **RDS → Create database**

- Engine: **PostgreSQL** (15 or 16 both fine, EF Core driver supports either)
- Templates: **Free tier**
- DB instance identifier: `catalog-demo-db`
- Master username: `catalogadmin`
- Master password: *(generate, save to a password manager)*
- Instance class: `db.t4g.micro` (the only free-tier option for PG)
- Storage: 20 GiB gp3, autoscaling off
- **Public access: Yes** (App Runner egress can't reach VPC-only RDS without extra wiring; for a demo this is acceptable)
- VPC security group: create new, name it `catalog-demo-db-sg`
- Initial database name: `catalog`

After creation:
- Edit the `catalog-demo-db-sg` security group → inbound rule → **Postgres (5432)** from **Anywhere-IPv4** (or, tighter: only App Runner's egress IPs, but those are dynamic — for a demo, 0.0.0.0/0 + a strong password is the pragmatic call. Delete the database when you're done.)
- Note the endpoint hostname (e.g. `catalog-demo-db.xxxxx.us-east-1.rds.amazonaws.com`)

## 2. ECR repository

Console: **ECR → Create repository**

- Visibility: **Private**
- Repository name: `catalog-api`
- Image tag mutability: **Mutable** (we re-tag `:latest` on every deploy)
- Scan on push: on (free)

Note the repository URI (e.g. `123456789012.dkr.ecr.us-east-1.amazonaws.com/catalog-api`).

## 3. IAM role for GitHub Actions (OIDC)

Avoids storing long-lived AWS access keys in GitHub secrets — GitHub Actions exchanges an OIDC token for short-lived AWS credentials at runtime.

a. **Add GitHub as an OIDC provider** (one-time per AWS account)

Console: **IAM → Identity providers → Add provider**
- Provider type: **OpenID Connect**
- Provider URL: `https://token.actions.githubusercontent.com`
- Audience: `sts.amazonaws.com`

b. **Create the role**

Console: **IAM → Roles → Create role**
- Trusted entity: **Web identity** → the provider you just added
- Audience: `sts.amazonaws.com`
- GitHub organization: *(your GitHub user/org)*
- GitHub repository: `NextAurora`
- Branch: `main` (or leave blank to allow any branch — tighter is better)

Attach a policy with these permissions (inline policy named `catalog-demo-deploy`):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "EcrAuth",
      "Effect": "Allow",
      "Action": ["ecr:GetAuthorizationToken"],
      "Resource": "*"
    },
    {
      "Sid": "EcrPushPull",
      "Effect": "Allow",
      "Action": [
        "ecr:BatchCheckLayerAvailability",
        "ecr:BatchGetImage",
        "ecr:GetDownloadUrlForLayer",
        "ecr:InitiateLayerUpload",
        "ecr:UploadLayerPart",
        "ecr:CompleteLayerUpload",
        "ecr:PutImage"
      ],
      "Resource": "arn:aws:ecr:us-east-1:*:repository/catalog-api"
    },
    {
      "Sid": "AppRunnerDeploy",
      "Effect": "Allow",
      "Action": [
        "apprunner:ListServices",
        "apprunner:StartDeployment"
      ],
      "Resource": "*"
    }
  ]
}
```

Role name: `github-actions-catalog-demo`. Copy its ARN.

c. **Add the role ARN as a repo secret**

GitHub: **Settings → Secrets and variables → Actions → New repository secret**
- Name: `AWS_DEPLOY_ROLE_ARN`
- Value: the ARN from step b

## 4. App Runner service

This step requires an image already in ECR. Run the workflow once first (it pushes `:latest`), then come back here.

Console: **App Runner → Create service**

- Source: **Container registry → Amazon ECR**
- Image URI: `123456789012.dkr.ecr.us-east-1.amazonaws.com/catalog-api:latest`
- Deployment trigger: **Automatic** (App Runner polls ECR for new `:latest` pushes)
- ECR access role: let App Runner create one (default: `AppRunnerECRAccessRole`)
- Service name: `catalog-api-demo`
- CPU: **0.25 vCPU**, Memory: **0.5 GB** (cheapest tier)
- Port: `8080`
- **Environment variables**:
  ```
  ASPNETCORE_ENVIRONMENT      = Production
  DemoMode                    = true
  ConnectionStrings__catalog-db = Host=<RDS endpoint>;Port=5432;Database=catalog;Username=catalogadmin;Password=<from step 1>;SSL Mode=Require;Trust Server Certificate=true
  ```
- Health check path: `/health`
- Auto scaling: minimum 1, maximum 1 (no need to scale a demo)

After creation, App Runner gives you a public HTTPS URL like `https://xxxx.us-east-1.awsapprunner.com`.

## 5. Verify

```bash
URL=https://<your-app-runner-domain>
curl -sS $URL/health                  # → "Healthy"
curl -sS $URL/api/v1/products         # → JSON product list (empty initially)
open $URL/scalar/v1                   # → Scalar interactive API UI
```

If `/scalar/v1` 404s, the `DemoMode` env var didn't take — re-check the App Runner config.

## Deploying updates (AWS path)

After the one-time setup, every deploy is one click:

GitHub: **Actions → DEPLOY_CATALOG_DEMO → Run workflow**

The workflow builds, pushes to ECR, and starts an App Runner deployment. ~3-5 min end to end.

## Tear-down (AWS path)

To stop the meter:
- App Runner → pause or delete the service (delete is irreversible but cheaper than even the pause state)
- RDS → delete `catalog-demo-db` (uncheck "create final snapshot" if you don't care about the data)
- ECR → optional, the storage cost is pennies/month

The IAM role and OIDC provider are free to leave in place for next time.

---

## What this deployment demonstrates

Useful when walking someone through the deployment, or as a refresher when you come back to this later.

- **"Cheap single-service demo — the production plan in [architecture.md](architecture.md) targets AWS SNS+SQS for the messaging fabric replacing Azure Service Bus, but that's a 2-3 service deployment that doesn't fit a free-tier budget."**
- **"Scalar exposed via a `DemoMode` flag — normally dev-only in `Program.cs` because OpenAPI specs are reconnaissance gold. The flag default is off, so production posture is unchanged."**
- **"HybridCache degrades to L1-only (in-process MemoryCache) when no `cache` connection string is set — registration is conditional. Real prod would add a managed Redis for L2 and pick up the same code path."**
- **"Same `Dockerfile.catalog` deploys to either Fly.io or AWS App Runner — the only thing that varies is the orchestration layer, which is exactly the abstraction containers buy you."**
- **(AWS path only)** **"OIDC instead of long-lived AWS keys in GitHub — short-lived STS credentials only valid for the job's lifetime, scoped via trust policy to this repo + branch."**
