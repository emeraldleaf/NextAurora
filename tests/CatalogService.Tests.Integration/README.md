# CatalogService.Tests.Integration

Integration tests that boot the **real** CatalogService API in-process (via
`WebApplicationFactory<Program>`) against **real infrastructure** — a PostgreSQL
container and a Redis container, both spun up by [Testcontainers](https://dotnet.testcontainers.org/).

Unit tests (`CatalogService.Tests.Unit`) substitute the repository, the cache, and the
DB. These tests don't — they exercise the seams that only break against the real thing:

| Test | What it proves |
|---|---|
| `Api_boots_and_migrations_apply` | The DI composition root in `Program.cs` boots; EF migrations apply cleanly against a fresh Postgres. |
| `GetProductById_caches_the_result_across_calls` | `HybridProductCache` actually caches over real Redis — after the row is deleted from the DB, the read still succeeds from cache. |
| `UpdateProduct_invalidates_the_cached_entry` | The write path (`UpdateProductHandler`) actually calls `IProductCache.InvalidateAsync` — the next read reflects the update. |
| `ConcurrencyToken_rejects_the_second_of_two_racing_writes` | The Postgres `xmin` concurrency token fires — two racing writes, the second throws `DbUpdateConcurrencyException`. |

## Running locally

You need **Docker running**. Then:

```bash
dotnet test tests/CatalogService.Tests.Integration/CatalogService.Tests.Integration.csproj
```

### macOS Docker Desktop gotcha

Docker Desktop on macOS puts its socket at `~/.docker/run/docker.sock`, **not** the
standard `/var/run/docker.sock`. The `docker` CLI knows this via its context;
Testcontainers does not auto-detect it and fails fast with `DockerUnavailableException`
even though `docker ps` works fine.

Fix — one of:

```bash
# Option A: point Testcontainers at the real socket for the test run
DOCKER_HOST="unix://$HOME/.docker/run/docker.sock" \
  dotnet test tests/CatalogService.Tests.Integration/CatalogService.Tests.Integration.csproj

# Option B (permanent): Docker Desktop → Settings → Advanced →
#   "Allow the default Docker socket to be used" — creates the /var/run/docker.sock symlink
```

CI (`ubuntu-latest`) ships Docker at the standard path, so the `integration-tests` job in
`.github/workflows/ci.yml` needs no override.

## Structure

- **`CatalogApiFactory`** — `WebApplicationFactory<Program>` + `IAsyncLifetime`. Starts the
  Postgres + Redis containers, injects their connection strings into the app's configuration,
  and registers `TestAuthHandler` as the auth scheme so `.RequireAuthorization()` endpoints
  are reachable without an identity provider.
- **`TestAuthHandler`** — always-succeeds auth handler. Real auth is JWT-against-Keycloak,
  which is irrelevant to what these tests cover.
- **`ProductCachingTests`** — the tests. Each uses a fresh product GUID so the per-class
  shared containers stay isolated without a DB reset between tests.

## Adding more

This is the CatalogService slice — the proven harness pattern. The heavier saga/messaging
integration tests (Wolverine outbox staging, cross-service choreography) need a RabbitMQ
broker (Testcontainer) and are a separate, larger effort. See
[docs/STATUS.md](../../docs/STATUS.md).
