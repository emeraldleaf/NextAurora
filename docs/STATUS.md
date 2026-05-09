# NextAurora — Project Status

> **Read this first when picking up work.** It's the cross-session entry point: where the project is right now, what to do next, and where the deeper docs live. Keep it short (~100 lines). Update it at the start or end of each working session.

**Last updated:** 2026-05-03

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

### Build / test state
- `dotnet build` — clean, 0 warnings, 0 errors.
- `dotnet test` — 133/133 unit tests pass.
- **No integration tests** — runtime correctness of the saga / outbox / migrations has not been verified.

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

1. **Integration tests** (Testcontainers-based saga test). Locks in correctness for everything we just built. Architecture.md still lists this as "Not Yet Implemented".
2. **Order cancellation flow** — listed in BRD as ORD-08, "Not Yet Implemented".
3. **Saga compensation** — failed-payment / failed-shipment rollback. Larger.
4. **Frontend implementation** — Storefront + SellerPortal scaffolds → real UIs. Big investment.

---

## Open issues / known gaps

- **Two recent commits have generic messages** (`Refactor code structure for improved readability and maintainability`). The architectural detail is recoverable from this doc + [performance-and-data-correctness.md "What changed when"](performance-and-data-correctness.md#what-changed-when), but `git log` alone won't tell the story. Future commits should use real messages.
- **Build artifacts (`bin/`, `obj/`) appear to be tracked** — see `git status -uall` after a clean. Add to `.gitignore` if not already, and untrack what's in.
- **Production migration deploy step** not yet automated. Tooling exists; deploy automation doesn't. See [perf guide](performance-and-data-correctness.md#resolved-migration-tooling-wired-up).
- **Integration tests** — none. Outbox semantics, concurrency-retry behavior, saga choreography aren't unit-testable.
- **`dotnet ef` tools 9.0.8 vs runtime 10.0.2** — non-fatal advisory. `dotnet tool update --global dotnet-ef` when convenient.
- **Service-to-service auth** (mTLS or per-service tokens) not configured. Fine inside the Aspire mesh; matters in production.

---

## Source-of-truth links

| Topic | File |
|---|---|
| Always-on rules every PR follows | [CLAUDE.md](../CLAUDE.md) |
| Performance / data correctness — *why* every rule exists | [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) |
| Architecture, services, communication patterns | [docs/architecture.md](architecture.md) |
| CQRS handler inventory + AsNoTracking strategy | [docs/cqrs-data-access.md](cqrs-data-access.md) |
| Functional & non-functional requirements | [docs/BRD.md](BRD.md) |
| Context propagation (CorrelationId/UserId/SessionId) | [docs/context-propagation.md](context-propagation.md) |
| .NET / EF Core deep guidance | [.claude/skills/dotnet-performance/SKILL.md](../.claude/skills/dotnet-performance/SKILL.md) |
