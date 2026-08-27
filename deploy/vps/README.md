# VPS deployment — what's on the box

Source of truth for `/root/nextaurora/` on the shared Hetzner box (`ubuntu-8gb-hel1-1`,
204.168.183.115). The plan and the reasoning live in
[docs/full-saga-deployment-plan.md](../../docs/full-saga-deployment-plan.md) (revision
2026-08-27: `docker compose` + the box's existing Caddy, not Dokploy).

| File | On the box | Purpose |
|---|---|---|
| `docker-compose.infra.yml` | `/root/nextaurora/` | Postgres (catalog/shipping/keycloak DBs), SQL Server Express (1.3 GB cap), Keycloak 26 + realm import, RabbitMQ 4, Redis — all hard memory caps |
| `docker-compose.services.yml` | `/root/nextaurora/` | five services + storefront from GHCR, workstation GC, caps |
| `postgres-init/01-databases.sh` | `/root/nextaurora/postgres-init/` | creates the three databases on first init |
| `nextaurora.caddy` | `/root/nextaurora/caddy/` | public site blocks; imported by riparian's `Caddyfile.full` (`import /etc/caddy/nextaurora/*.caddy`) |
| `deploy.sh` + `.service` + `.timer` | `/root/nextaurora/`, `/etc/systemd/system/` | **pull-based deploy**: every 3 min `compose pull && up -d`; the box's SSH allowlist blocks Actions runners by design |
| *(not here)* `.env` | `/root/nextaurora/.env` (0600) | `PG_PASSWORD`, `MSSQL_SA_PASSWORD`, `RABBITMQ_PASSWORD`, `KEYCLOAK_ADMIN_PASSWORD` — generated on the box, never committed |
| *(not here)* realm | `/root/nextaurora/keycloak/` | copy of `NextAurora.AppHost/realms/nextaurora-realm.json`; re-import with `compose run --rm keycloak import --dir /opt/keycloak/data/import --override true` (stop keycloak first) |

**First-boot steps that aren't in compose:** SQL Server databases `orders` and `payments`
must exist before the services start (`sqlcmd ... CREATE DATABASE`), because Wolverine's
envelope-store setup runs before EF's migrations create anything.

**Changing a file here = copy it to the box** (`scp deploy/vps/<file> nextaurora-vps:/root/nextaurora/…`)
and `docker compose -f <file> up -d` (or `caddy reload` for the Caddy file). Image changes
need no hands: merging to main publishes to GHCR and the timer rolls them.
