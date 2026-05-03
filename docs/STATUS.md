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

**Active item:** smoke-run the system end-to-end. Run the Aspire AppHost and walk through the saga in the dashboard. Highest-ROI step before adding more code.

### Smoke-test checklist
```
□ dotnet run --project NextAurora.AppHost — Aspire dashboard opens
□ All services show "Running" (no startup crashes)
□ Each DB-using service logs successful migration apply (or "no pending migrations")
□ Wolverine logs envelope dispatcher start; `wolverine` schema visible in each service DB
□ POST /api/v1/products with seller token → 201
□ GET /api/products → 400 (version required, our policy)
□ GET /api/v1/products?page=1&pageSize=10 → 200, ≤10 items
□ POST /api/v1/orders with buyer token → 202
□ Aspire dashboard trace: Order → Payment → Shipping → Notification spans, all share one CorrelationId
□ Mid-saga: rows in `wolverine.outgoing_envelopes` with PublishedAt set after dispatch
```

### After the smoke run
Roughly highest-leverage first:

1. **Integration tests** (Testcontainers-based saga test). Locks in correctness for everything we just built. Architecture.md still lists this as "Not Yet Implemented".
2. **Distributed caching for Catalog** — Redis is wired in AppHost but unused. Cache-aside on product reads. ~1–2 hours.
3. **Order cancellation flow** — listed in BRD as ORD-08, "Not Yet Implemented".
4. **Saga compensation** — failed-payment / failed-shipment rollback. Larger.
5. **Frontend implementation** — Storefront + SellerPortal scaffolds → real UIs. Big investment.

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
