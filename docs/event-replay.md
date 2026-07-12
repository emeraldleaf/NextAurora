# Event Replay

> **Status: removed and replaced.** The hand-rolled `EventLogs` table and `/admin/events/...` endpoints from earlier versions of this guide were deleted as dead code post-Wolverine. They only ever recorded *replays of replays* — the original events were never logged because the `LoggingEventPublisher` that would have written them was removed during the Wolverine migration.

## What replaced it

[Wolverine's transactional outbox](performance-and-data-correctness.md#resolved-transactional-outbox-via-wolverine) is the canonical answer. Each event-publishing service (Order, Payment, Shipping) has a `wolverine` schema in its own DB containing:

| Table | What it holds |
|---|---|
| `wolverine.outgoing_envelopes` | Messages staged in a DB transaction but not yet flushed to the broker. The "in-flight" queue. |
| `wolverine.incoming_envelopes` | Inbox: deduplication record for received messages. |
| `wolverine.dead_letters` | Messages that exhausted retries and are no longer being processed. |

The schema is auto-created on app startup via `builder.Services.AddResourceSetupOnStartup()`.

## Inspecting state in dev

Aspire spins up the databases as containers, so you can connect with any client. Connection strings appear in the Aspire dashboard's Resources tab.

**Postgres services** (Catalog, Shipping):

```sql
-- What's staged but not yet sent?
SELECT id, message_type, attempts, scheduled_time
FROM wolverine.outgoing_envelopes
ORDER BY id DESC LIMIT 50;

-- What got DLQd?
SELECT id, message_type, exception_type, exception_message
FROM wolverine.dead_letters
ORDER BY id DESC LIMIT 50;
```

**SQL Server services** (Order, Payment): same tables, identical shape, accessed via Microsoft.Data.SqlClient or sqlcmd.

A non-empty `outgoing_envelopes` for more than a few seconds usually means the bus dispatcher is wedged — check the service logs and Aspire dashboard for transport errors. A growing `dead_letters` count is a real correctness signal: the same messages are failing past their retry budget.

## Programmatic access via IMessageStore

For tooling, scripts, and any future operator UI: use Wolverine's `IMessageStore` API rather than querying the schema directly. It's the supported abstraction and survives schema migrations.

```csharp
public class OutboxInspector(IMessageStore store)
{
    public async Task<IReadOnlyList<Envelope>> GetPendingAsync(CancellationToken ct) =>
        await store.Outbox.LoadOutgoingAsync();
}
```

Resending from the DLQ, requeueing scheduled messages, and pausing a sending endpoint are all `IMessageStore` operations. If you need an admin UI later, build it on top of this — don't restore the old `EventLogs` design.

## Related

- [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) — outbox decisions, dual-write problem, retry policies.
- [docs/event-driven-observability.md](event-driven-observability.md) — distributed tracing, event end-to-end.
- [docs/observability.md](observability.md) — metrics (`messages.abandoned`), logs, traces.
- [docs/architecture.md](architecture.md#event-driven-architecture) — event topology and the choreography saga.
