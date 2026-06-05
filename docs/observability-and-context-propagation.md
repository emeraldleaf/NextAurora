# Observability & Context Propagation

Deep-dive companion to [CLAUDE.md "Observability & Context Propagation"](../CLAUDE.md#observability--context-propagation), which keeps the always-on traps and points here for mechanism + wiring detail.

NextAurora propagates three context identifiers across HTTP and Service Bus
boundaries so every log line, span, and event in a request's lifecycle can
be correlated:

| Concept | Activity Baggage Key | HTTP / SB Property | Logger Scope Key |
|---|---|---|---|
| Correlation | `correlation.id` | `X-Correlation-Id` | `CorrelationId` |
| User | `user.id` | `X-User-Id` | `UserId` |
| Session | `session.id` | `X-Session-Id` | `SessionId` |

**Sources:**

- `correlation.id` — from the `X-Correlation-Id` request header, or generated from the trace ID when absent.
- `user.id` — from the `ClaimTypes.NameIdentifier` JWT claim (`sub`); null when unauthenticated.
- `session.id` — from the `X-Session-Id` request header (client-generated browser/app session UUID); null if not provided.

All three are set by `CorrelationIdMiddleware` (HTTP entry point) and by `ContextPropagationMiddleware` (Wolverine incoming-message middleware, async entry point). All three are propagated onto outgoing Wolverine messages by `OutgoingContextMiddleware`. Both middlewares are wired via the `opts.AddNextAuroraContextPropagation()` extension in each service's `Program.cs`.

## HTTP middleware order — strict

`CorrelationIdMiddleware` reads `ClaimTypes.NameIdentifier` from `context.User` to populate the `UserId` scope. That requires running AFTER `UseAuthentication` (otherwise `context.User` is empty and `UserId` is silently always null — defeats the audit pipeline). It also must run BEFORE `UseAuthorization` so the `UserId` scope is active during the authorization decision — any 401/403 denial gets logged with the authenticated user's ID, preserving the audit trail for "user X tried to access resource they shouldn't."

Canonical order in `MapDefaultEndpoints`:

```csharp
app.UseExceptionHandler();                          // wraps every error below
app.UseAuthentication();                            // populates context.User
app.UseMiddleware<CorrelationIdMiddleware>();       // reads User, opens log scope
app.UseAuthorization();                             // 401/403 attributed to UserId
```

Reference: [Extensions.cs `MapDefaultEndpoints`](../NextAurora.ServiceDefaults/Extensions.cs).

## Wolverine middleware classes must use instance methods

`opts.Policies.AddMiddleware<T>()` only discovers `Before`/`After`/`Finally` (and their `Async` variants) as **instance methods** on a public class with a public constructor. Static methods aren't discovered — registration throws `InvalidWolverineMiddlewareException` at host startup. This applies even when the method has no instance state. Suppress S2325 ("should be static") with a `Justification` referencing this rule rather than satisfying the analyzer.

## Wolverine pipeline scope

`ContextPropagationMiddleware` opens a `logger.BeginScope()` before invoking each handler so **every log line emitted anywhere in the handler** carries `CorrelationId`, `UserId`, and `SessionId` automatically. Wolverine's `Policies.LogMessageStarting()` adds handler name + elapsed time on top of that.

Order in the Wolverine pipeline:

1. FluentValidation policy (`opts.UseFluentValidation()`) — rejects invalid commands before handlers run
2. `ContextPropagationMiddleware` — opens logger scope
3. Handler
4. `opts.Policies.AutoApplyTransactions()` — wraps each EF-touching handler chain so outgoing messages are persisted to the outbox in the same DB transaction as the entity write

## Wolverine envelope context extraction

Handlers don't extract context manually — `ContextPropagationMiddleware` does it for them. The middleware reads `Envelope.Headers["X-Correlation-Id" | "X-User-Id" | "X-Session-Id"]` (Wolverine's transport-agnostic header bag, mapped to Service Bus `ApplicationProperties` over the wire), restores them into Activity baggage, and opens a `logger.BeginScope()`. After the handler runs, `Finally()` disposes the scope.

Outgoing context is stamped by `OutgoingContextMiddleware`, which reads Activity baggage and writes the same headers onto outgoing envelopes. The full mechanism is registered via `opts.AddNextAuroraContextPropagation()` in each service's `Program.cs`.

**Structured logging scope hygiene:** never add null/empty keys to logging scope dictionaries — use `if (x is not null) scope["Key"] = x`. Always pass `StringComparer.Ordinal` when constructing `Dictionary<string, T>` (per Meziantou MA0002).

## Transactional Outbox (Wolverine)

Each event-publishing service (Order, Payment, Shipping) runs Wolverine's transactional outbox. Outgoing events are persisted to a `wolverine.*` schema in the same DB transaction as the entity write, then dispatched to Azure Service Bus by Wolverine's background flush. Configuration lives in each service's `Program.cs`:

```csharp
opts.PersistMessagesWithSqlServer(connectionString, "wolverine");   // or PersistMessagesWithPostgresql
opts.UseEntityFrameworkCoreTransactions();
opts.Policies.AutoApplyTransactions();
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
```

`builder.Services.AddResourceSetupOnStartup()` auto-creates outbox tables on app startup. This means the entity write and the event publish either both commit or neither does — no more lost events on bus failure or process crash. Full rationale + failure modes: [docs/performance-and-data-correctness.md](performance-and-data-correctness.md).

### Outbox outside a Wolverine handler — atomicity trap

`AutoApplyTransactions` only wraps Wolverine handler chains. Code that runs OUTSIDE a handler (`BackgroundService` sweepers, cron-style recovery jobs, admin endpoints, anything publishing events from a non-handler context) does NOT get the outbox-atomic transaction wrap for free.

The trap: `IMessageBus.PublishAsync(...)` stages an envelope into the `wolverine.outgoing_envelopes` tracker, but **the envelope is only persisted when `SaveChangesAsync` runs after the publish call**. Wolverine's `UseEntityFrameworkCoreTransactions` intercepts `SaveChanges` to bridge the staged envelope into the DB transaction. If your wrapper does `BeginTransactionAsync` → entity write + publish → `Commit` *without an explicit `SaveChangesAsync` between the publish and the commit*, the envelope stays in the tracker, the transaction commits without it, and the event is silently dropped.

The canonical safe wrapper:

```csharp
public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
{
    await using var tx = await context.Database.BeginTransactionAsync(ct);
    await work(ct);                          // entity write + PublishAsync inside here
    await context.SaveChangesAsync(ct);      // flushes Wolverine's staged envelope
    await tx.CommitAsync(ct);
}
```

Reference: [PaymentRecoveryJob](../PaymentService/Infrastructure/PaymentRecoveryJob.cs) — the canonical inline implementation of this wrapper (the previous `IPaymentRepository.ExecuteInTransactionAsync` wrapper was deleted in the simplicity refactor; the pattern is unchanged, just inlined). When adding a non-handler code path that publishes events, **either** wrap it in this pattern **or** factor the publish back into a Wolverine handler triggered by an internal scheduled message.

## Event Replay

Replay is handled through Wolverine's own message-store and DLQ tooling. The previous hand-rolled `EventLogs` table and `/admin/events` endpoints were deleted as dead code post-Wolverine — they were only ever populated by replay records of replays. If operator-facing event browsing is needed, build it on top of `IMessageStore` (Wolverine's API) or the `wolverine.outgoing_envelopes` table directly.
