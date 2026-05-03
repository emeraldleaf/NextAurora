# Event Replay

> **Status: removed.** The hand-rolled `EventLogs` table and `/admin/events/...` endpoints described in earlier versions of this doc were deleted as dead code post-Wolverine. They were only ever populated with replay records of replays — the original events were never logged because the `LoggingEventPublisher` that would have written them was removed during the Wolverine migration.

## What replaced it

Wolverine's transactional outbox (configured in [docs/performance-and-data-correctness.md](performance-and-data-correctness.md#resolved-transactional-outbox-via-wolverine)) provides:

- A `wolverine` schema in each event-publishing service's database (Order, Payment, Shipping) with `outgoing_envelopes`, `dead_letters`, and related tables.
- Automatic retry with backoff on transient failures.
- A `DbUpdateConcurrencyException` retry policy via `AddConcurrencyRetry()`.
- Dead-letter queue tracking via the `messages.abandoned` metric.

## If you need replay

For now, query the `wolverine` schema directly or use Wolverine's `IMessageStore` API. If operator-facing replay UIs become a real need, build them on top of `IMessageStore` rather than restoring the old `EventLogs` design.

## Related

- [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) — outbox decisions and rationale.
- [docs/event-driven-observability.md](event-driven-observability.md) — tracing events end-to-end.
- [docs/observability.md](observability.md) — metrics, logs, traces.
