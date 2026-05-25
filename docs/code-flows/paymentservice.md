# PaymentService — code flow walkthrough

> **What this is.** A walk through the code paths a new contributor will hit first in [PaymentService](../../PaymentService/). PaymentService is the **saga middle step** — it receives `OrderPlacedEvent` from OrderService over Service Bus, charges via a payment gateway (Stripe), and publishes `PaymentCompletedEvent` or `PaymentFailedEvent`. A `BackgroundService` recovery sweeper handles the "stuck in Pending" case where the gateway hangs.
>
> **Architecture style:** Vertical Slice Architecture (single csproj). Folders: [`Endpoints/`](../../PaymentService/Endpoints), [`Features/`](../../PaymentService/Features), [`Domain/`](../../PaymentService/Domain), [`Infrastructure/`](../../PaymentService/Infrastructure). Composition root: [`Program.cs`](../../PaymentService/Program.cs).
>
> **Three flows to understand:**
> 1. **Saga consume + cascade** — `OrderPlacedEvent` → tiny translator → `ProcessPaymentCommand` → handler charges gateway → publishes outcome event.
> 2. **HTTP admin path** — same `ProcessPaymentCommand` reachable from `POST /api/v1/payments/process` for manual processing.
> 3. **`PaymentRecoveryJob`** — periodic sweeper that catches Pending payments stuck past the stale threshold, wrapping the mark-failed + publish in an explicit `BeginTransactionAsync` → `SaveChangesAsync` → `CommitAsync` so the entity write + outbox envelope stay atomic outside the Wolverine pipeline.

---

## Flow 1 — Saga: `OrderPlacedEvent` → process payment → publish outcome

```mermaid
sequenceDiagram
    autonumber
    participant ASB1 as Azure Service Bus<br/>(orders topic)
    participant W1 as Wolverine consumer +<br/>ContextPropagation middleware
    participant OPH as OrderPlacedHandler<br/>Features/OrderPlacedHandler.cs<br/>(static, returns command)
    participant Val as ProcessPaymentCommandValidator<br/>Features/ProcessPayment.cs<br/>(FluentValidation)
    participant H as ProcessPaymentHandler<br/>Features/ProcessPayment.cs
    participant Ctx as PaymentDbContext<br/>Infrastructure/Data/PaymentDbContext.cs
    participant Agg as Payment aggregate<br/>Domain/Payment.cs
    participant GW as IPaymentGateway<br/>Infrastructure/Gateway/<br/>StripePaymentGateway.cs
    participant Pub as IEventPublisher<br/>Infrastructure/WolverineEventPublisher.cs
    participant DB as SQL Server +<br/>wolverine.outgoing_envelopes
    participant ASB2 as Azure Service Bus<br/>(payments topic)

    ASB1->>W1: OrderPlacedEvent
    Note over W1: restores logger scope from<br/>envelope headers (CorrelationId,<br/>UserId, SessionId)
    W1->>OPH: Handle(@event)
    OPH-->>W1: returns ProcessPaymentCommand<br/>(Wolverine cascading message —<br/>no IMessageBus call needed)
    Note over W1: same Wolverine pipeline,<br/>same DB tx wraps this step too
    W1->>Val: validate command
    Val-->>W1: ok
    W1->>H: HandleAsync(command, ct)<br/>(AutoApplyTransactions wraps)

    H->>Ctx: context.Payments.FirstOrDefaultAsync(<br/>  p => p.OrderId == orderId, ct)
    Ctx->>DB: SELECT * FROM payments<br/>WHERE order_id = @id (tracked)
    DB-->>H: Payment (tracked) or null

    alt existing payment found — idempotency
        H-->>W1: existing.Id (early return)
        Note over H: at-least-once delivery,<br/>DLQ replays, double admin POSTs —<br/>all no-op here
    else no existing — create new
        H->>Agg: Payment.Create(orderId, buyerId,<br/>amount, currency, "Stripe")
        H->>Ctx: context.Payments.AddAsync(payment, ct)
        H->>Ctx: context.SaveChangesAsync(ct)
        Ctx->>DB: INSERT payments (status=Pending)

        H->>GW: ProcessPaymentAsync(amount, currency, ct)
        GW-->>H: GatewayResult { Success, TransactionId or ErrorMessage }

        alt Success
            H->>Agg: MarkAsCompleted(transactionId)
            Note over Agg: throws if status != Pending<br/>(state guard prevents<br/>double-completion)
            H->>Pub: PublishAsync(PaymentCompletedEvent)
        else Failed
            H->>Agg: MarkAsFailed(errorMessage)
            H->>Pub: PublishAsync(PaymentFailedEvent)
            Note over Pub: error message kept verbatim for<br/>OrderService's audit trail —<br/>never returned to clients
        end

        H->>Ctx: context.SaveChangesAsync(ct)
        Note over Ctx,DB: AutoApplyTransactions wraps —<br/>UPDATE payments + outbox envelope<br/>in ONE DB tx
        DB-->>H: tx commit
        DB->>ASB2: dispatched to ASB<br/>(payments topic)
    end
```

**Why two handlers (`OrderPlacedHandler` + `ProcessPaymentHandler`).** The event handler is a 2-line translator that converts an event into a command and returns it. Wolverine's "cascading messages" feature picks up the return value and runs its handler next — the same `ProcessPaymentHandler` reached from the HTTP admin endpoint. One business rule, multiple entry points.

**Idempotency via existence check + unique index.** The `context.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId)` lookup short-circuits on existing rows for retry/redelivery scenarios. The unique index on `OrderId` in [PaymentDbContext](../../PaymentService/Infrastructure/Data/PaymentDbContext.cs) is the DB backstop if two redeliveries race past the check at the same instant.

---

## Flow 2 — HTTP `POST /api/v1/payments/process` (admin path)

Same handler, same outcome — different entry point.

```mermaid
sequenceDiagram
    autonumber
    actor Admin
    participant EP as PaymentEndpoints<br/>Endpoints/PaymentEndpoints.cs
    participant Bus as IMessageBus
    participant H as ProcessPaymentHandler<br/>Features/ProcessPayment.cs

    Admin->>EP: POST /api/v1/payments/process<br/>{ OrderId, Amount, Currency, BuyerId }
    EP->>Bus: bus.InvokeAsync<Guid>(command, ct)
    Bus->>H: same handler as Flow 1
    Note over H: idempotency check + gateway call +<br/>state transition + event publish<br/>(identical to Flow 1)
    H-->>Bus: payment.Id
    Bus-->>EP: payment.Id
    EP-->>Admin: 200 OK + { Id }
```

The admin endpoint matters because it's how an operator manually triggers a payment when the saga is unavailable (e.g. Service Bus outage). Same code path; same outbox guarantees.

---

## Flow 3 — `PaymentRecoveryJob` (BackgroundService sweeper)

A Pending payment stuck past the stale threshold (default ~5 min — see [PaymentRecoveryOptions](../../PaymentService/Infrastructure/PaymentRecoveryOptions.cs)) is what you get when the gateway call hangs and the process dies mid-transaction. The next pod restart can't recover via the saga because `ProcessPaymentHandler`'s existence check would no-op on the half-written row. The sweeper handles these.

```mermaid
sequenceDiagram
    autonumber
    participant Tick as PaymentRecoveryJob<br/>Infrastructure/PaymentRecoveryJob.cs<br/>(BackgroundService loop)
    participant Lock as DistributedLock.SqlServer<br/>(sp_getapplock)
    participant Scope as fresh IServiceScope<br/>per iteration
    participant Ctx as PaymentDbContext
    participant Agg as Payment aggregate
    participant Pub as IEventPublisher
    participant DB as SQL Server +<br/>wolverine.outgoing_envelopes
    participant ASB as Azure Service Bus

    loop every SweepInterval (default 60s)
        Tick->>Lock: TryAcquireLockAsync("payments-recovery",<br/>TimeSpan.Zero)
        alt another replica holds it
            Lock-->>Tick: null → skip this tick
        else acquired
            Tick->>Scope: create fresh DI scope<br/>(NOT reused across iterations —<br/>change-tracker stays small)
            Scope-->>Tick: PaymentDbContext + IEventPublisher
            Tick->>Ctx: context.Payments.AsNoTracking()<br/>.Where(Status==Pending && CreatedAt<threshold)<br/>.Select(p => p.Id).ToListAsync(ct)
            Ctx->>DB: SELECT id FROM payments<br/>WHERE status = Pending<br/>AND created_at < @threshold
            DB-->>Tick: Guid[]

            loop foreach stale id
                Tick->>Ctx: context.Payments.FirstOrDefaultAsync(<br/>  p => p.Id == id, ct)
                Ctx->>DB: SELECT (tracked) + RowVersion
                DB-->>Tick: Payment

                alt status != Pending — race already resolved
                    Note over Tick: another sweeper iteration<br/>or ProcessPayment completed<br/>between query and load
                else still Pending
                    Note over Tick,DB: EXPLICIT TRANSACTION WRAP —<br/>SAME tx for entity write + envelope.<br/>BackgroundService runs OUTSIDE<br/>Wolverine's handler pipeline so<br/>AutoApplyTransactions does NOT apply<br/>here — must wrap manually.
                    Tick->>Ctx: context.Database.BeginTransactionAsync(ct)
                    Tick->>Agg: MarkAsFailed("timed out — recovery sweep")
                    Tick->>Pub: PublishAsync(PaymentFailedEvent, ct)
                    Note over Pub: stages outbox envelope in EF<br/>change tracker (not yet persisted)
                    Tick->>Ctx: context.SaveChangesAsync(ct)
                    Note over Ctx,DB: flushes BOTH the MarkAsFailed mutation<br/>AND the staged outbox envelope into<br/>the ambient transaction
                    Tick->>Ctx: tx.CommitAsync(ct)
                    DB-->>Tick: tx commit (both rows or neither)
                    DB->>ASB: PaymentFailedEvent dispatched
                end

                alt DbUpdateConcurrencyException
                    Note over Tick: another process won the<br/>RowVersion race. Roll back —<br/>no further action needed.
                end
            end
        end
    end
```

**The outbox-outside-handler trap.** Wolverine's `AutoApplyTransactions` policy wraps **handler chains** — anything dispatched through `IMessageBus.InvokeAsync`/`PublishAsync` into a handler gets the wrap automatically. That includes the admin path in Flow 2 (`POST /payments/process` → `bus.InvokeAsync<Guid>(command)` → `ProcessPaymentHandler`) — it's still inside the Wolverine pipeline, so the wrap applies. The trap is code that publishes events **without entering a handler at all**: `BackgroundService` sweep loops, cron jobs, or any future code path that does `bus.PublishAsync(@event)` outside an active Wolverine handler context. `PublishAsync` stages an envelope into the in-memory tracker, but the envelope is only persisted to `wolverine.outgoing_envelopes` when `SaveChangesAsync` runs after the publish. The canonical safe wrap (now inline in `PaymentRecoveryJob.RecoverOneAsync`, no longer behind a repository method):

```csharp
await using var tx = await context.Database.BeginTransactionAsync(ct);
// entity work...
payment.MarkAsFailed(reason);
await eventPublisher.PublishAsync(new PaymentFailedEvent { ... }, ct);
await context.SaveChangesAsync(ct);   // flushes BOTH entity mutation AND staged envelope
await tx.CommitAsync(ct);
```

See [`PaymentRecoveryJob.RecoverOneAsync`](../../PaymentService/Infrastructure/PaymentRecoveryJob.cs) and the rationale in [docs/performance-and-data-correctness.md](../performance-and-data-correctness.md). This pattern covers any future non-handler code that publishes events.

**Distributed lock.** Multiple PaymentService replicas could each fire their sweep tick at the same second. The `sp_getapplock`-based distributed lock ensures only one replica processes the sweep per tick. `TimeSpan.Zero` (no-wait) means replicas that don't acquire just skip the iteration — they'll try again at the next tick.

**`TimeProvider` injection.** Tests inject `FakeTimeProvider` to advance virtual time and exercise the stale-threshold logic deterministically. The sweep loop never reads `DateTime.UtcNow` directly.

---

## Payment aggregate — state machine

```mermaid
stateDiagram-v2
    [*] --> Pending: Payment.Create()<br/>(ProcessPaymentHandler)
    Pending --> Completed: MarkAsCompleted(transactionId)<br/>(gateway succeeded)
    Pending --> Failed: MarkAsFailed(reason)<br/>(gateway failed OR<br/>recovery sweep timeout)
    Completed --> [*]
    Failed --> [*]

    note right of Pending
        Initial state after AddAsync
        commits, BEFORE gateway call
    end note

    note right of Failed
        Two callers reach Failed —
        ProcessPaymentHandler (sync)
        and PaymentRecoveryJob
        (async sweep)
    end note
```

**Idempotency by throw (not no-op).** Unlike OrderService's `Order.MarkAsX` methods (which no-op on duplicate transitions), `Payment.MarkAsCompleted` and `MarkAsFailed` **throw `InvalidOperationException`** if status is not `Pending`. The reason: PaymentService's idempotency lives at the *handler* level (existence check on `OrderId`), not the aggregate level. By the time you're calling `MarkAsX` here, you've already loaded a freshly-created `Pending` payment in the current request — a non-Pending status means a serious bug, not a duplicate event.

---

## File inventory

| Path | Purpose |
|---|---|
| [Endpoints/PaymentEndpoints.cs](../../PaymentService/Endpoints/PaymentEndpoints.cs) | HTTP admin path: `POST /api/v1/payments/process` |
| [Features/OrderPlacedHandler.cs](../../PaymentService/Features/OrderPlacedHandler.cs) | Static event translator (Wolverine cascading message) |
| [Features/ProcessPayment.cs](../../PaymentService/Features/ProcessPayment.cs) | Command + validator + handler (idempotency + gateway + state + publish) |
| [Domain/Payment.cs](../../PaymentService/Domain/Payment.cs) | Aggregate root + state guards (throw on bad transition) |
| [Domain/PaymentStatus.cs](../../PaymentService/Domain/PaymentStatus.cs) | Enum: Pending / Completed / Failed |
| [Domain/IPaymentGateway.cs](../../PaymentService/Domain/IPaymentGateway.cs) | Anti-corruption layer port — substituted in tests |
| [Domain/IEventPublisher.cs](../../PaymentService/Domain/IEventPublisher.cs) | Event publish port (Wolverine impl) |
| [Infrastructure/Gateway/StripePaymentGateway.cs](../../PaymentService/Infrastructure/Gateway/StripePaymentGateway.cs) | Stripe adapter — translates SDK exceptions into `GatewayResult` |
| [Infrastructure/WolverineEventPublisher.cs](../../PaymentService/Infrastructure/WolverineEventPublisher.cs) | `IMessageBus.PublishAsync` adapter |
| [Infrastructure/PaymentRecoveryJob.cs](../../PaymentService/Infrastructure/PaymentRecoveryJob.cs) | `BackgroundService` sweep loop + distributed lock + per-iteration scope |
| [Infrastructure/PaymentRecoveryOptions.cs](../../PaymentService/Infrastructure/PaymentRecoveryOptions.cs) | Config record: SweepInterval, StaleThreshold, LockName |
| [Infrastructure/Data/PaymentDbContext.cs](../../PaymentService/Infrastructure/Data/PaymentDbContext.cs) | EF context — SQL Server `RowVersion` token, unique index on `OrderId` |
| [Program.cs](../../PaymentService/Program.cs) | Composition root — Wolverine + EF + `BackgroundService` registration |

---

## See also

- [docs/code-flows/orderservice.md](orderservice.md) — OrderService publishes `OrderPlacedEvent` (Flow 1's input) and consumes `PaymentCompletedEvent`/`PaymentFailedEvent` (Flow 1's output)
- [docs/transactional-outbox.svg](../transactional-outbox.svg) — diagram of outbox mechanics; the recovery job's inline `BeginTransactionAsync` → `SaveChangesAsync` → `CommitAsync` is the non-handler variant of the same pattern
- [docs/performance-and-data-correctness.md](../performance-and-data-correctness.md) — full perf rationale incl. outbox + sweeper patterns
- [docs/event-catalog.md](../event-catalog.md) — every event's shape and producer/consumer
