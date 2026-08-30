# PaymentService — code flow walkthrough

> **What this is.** A walk through the code paths a new contributor will hit first in [PaymentService](../../PaymentService/). PaymentService is the **saga middle step** — it receives `OrderPlacedEvent` from OrderService over RabbitMQ, charges via a payment gateway (Stripe), and publishes `PaymentCompletedEvent` or `PaymentFailedEvent`. A `BackgroundService` recovery sweeper handles the "stuck in Pending" case where the gateway hangs.
>
> **Architecture style:** Vertical Slice Architecture (single csproj). Folders: [`Endpoints/`](../../PaymentService/Endpoints), [`Features/`](../../PaymentService/Features), [`Domain/`](../../PaymentService/Domain), [`Infrastructure/`](../../PaymentService/Infrastructure). Composition root: [`Program.cs`](../../PaymentService/Program.cs).
>
> **Three flows to understand:**
> 1. **Saga consume + cascade** — `OrderPlacedEvent` → tiny translator → `ProcessPaymentCommand` → Acceptor persists `Pending` + publishes `PaymentProcessingRequested` → Gateway handler charges Stripe → publishes outcome event.
> 2. **HTTP admin path** — same `ProcessPaymentCommand` reachable from `POST /api/v1/payments/process` for manual processing.
> 3. **`PaymentRecoveryJob`** — periodic sweeper that catches Pending payments stuck past the stale threshold, wrapping the mark-failed + publish in Wolverine's non-handler outbox (`IDbContextOutbox`): `Enroll` → `PublishAsync` → `SaveChangesAndFlushMessagesAsync`, so the entity write + outbox envelope stay atomic outside the Wolverine pipeline.

---

## Flow 1 — Saga: `OrderPlacedEvent` → process payment → publish outcome

```mermaid
sequenceDiagram
    autonumber
    participant MQ1 as RabbitMQ<br/>(payment-orders queue, bound to the<br/>order-events fanout exchange)
    participant W1 as Wolverine consumer +<br/>ContextPropagation middleware
    participant OPH as OrderPlacedHandler<br/>Features/OrderPlacedHandler.cs<br/>(static, returns command)
    participant Val as ProcessPaymentCommandValidator<br/>Features/ProcessPayment.cs<br/>(FluentValidation)
    participant H as ProcessPaymentHandler (Acceptor)<br/>Features/ProcessPayment.cs
    participant Ctx as PaymentDbContext<br/>Infrastructure/Data/PaymentDbContext.cs
    participant Agg as Payment aggregate<br/>Domain/Payment.cs
    participant Msg as IMessageContext<br/>(Wolverine, method-injected)
    participant GH as PaymentProcessingRequestedHandler<br/>Features/ProcessPayment.cs<br/>(Gateway handler, Wolverine worker)
    participant GW as IPaymentGateway<br/>Infrastructure/Gateway/<br/>StripePaymentGateway.cs
    participant DB as SQL Server +<br/>wolverine.outgoing_envelopes
    participant MQ2 as RabbitMQ<br/>(payment-events fanout exchange)

    MQ1->>W1: OrderPlacedEvent
    Note over W1: restores logger scope from<br/>envelope headers (CorrelationId,<br/>UserId, SessionId)
    W1->>OPH: Handle(@event)
    OPH-->>W1: returns ProcessPaymentCommand<br/>(Wolverine cascading message —<br/>no IMessageBus call needed)
    Note over W1: same Wolverine pipeline,<br/>same DB tx wraps this step too
    W1->>Val: validate command
    Val-->>W1: ok
    W1->>H: HandleAsync(command, messageContext, ct)<br/>(AutoApplyTransactions wraps)

    H->>Ctx: context.Payments.FirstOrDefaultAsync(<br/>  p => p.OrderId == orderId, ct)
    Ctx->>DB: SELECT * FROM payments<br/>WHERE order_id = @id (tracked)
    DB-->>H: Payment (tracked) or null

    alt existing payment found — idempotency
        H-->>W1: existing.Id (early return)
        Note over H: at-least-once delivery,<br/>DLQ replays, double admin POSTs —<br/>all no-op here.<br/>On a terminal row (Completed/Failed)<br/>the Acceptor defensively re-publishes<br/>the outcome event via IEventPublisher<br/>(RepublishTerminalEventAsync)
    else no existing — accept
        H->>Agg: Payment.Create(orderId, buyerId,<br/>amount, currency, "Stripe")
        H->>Ctx: context.Payments.AddAsync(payment, ct)
        H->>Msg: PublishAsync(<br/>PaymentProcessingRequested(payment.Id))
        Note over Msg: enlisted in the handler's tx —<br/>envelope staged, not yet sent
        H->>Ctx: context.SaveChangesAsync(ct)
        Ctx->>DB: INSERT payments (status=Pending)<br/>+ outbox envelope in ONE DB tx
        DB-->>H: tx commit — Acceptor returns<br/>payment.Id here (no gateway call)
        DB->>GH: PaymentProcessingRequested<br/>dispatched after commit

        GH->>Ctx: load payment by Id (tracked)
        Note over GH: status guard — if not Pending,<br/>return (redelivery no-op)
        GH->>GW: ProcessPaymentAsync(amount, currency,<br/>payment.Id (idempotency key), ct)
        Note over GW: payment.Id rides as Stripe's<br/>Idempotency-Key — a redelivered<br/>message can't re-charge
        GW-->>GH: GatewayResult { Success, TransactionId or ErrorMessage }

        alt Success
            GH->>Agg: MarkAsCompleted(transactionId)
            Note over Agg: throws if status != Pending<br/>(state guard prevents<br/>double-completion)
            GH->>Msg: PublishAsync(PaymentCompletedEvent)
        else Failed
            GH->>Agg: MarkAsFailed(errorMessage)
            GH->>Msg: PublishAsync(PaymentFailedEvent)
            Note over Msg: error message kept verbatim for<br/>OrderService's audit trail —<br/>never returned to clients
        end

        GH->>Ctx: context.SaveChangesAsync(ct)
        Note over Ctx,DB: AutoApplyTransactions wraps —<br/>UPDATE payments + outbox envelope<br/>in ONE DB tx
        DB-->>GH: tx commit
        DB->>MQ2: dispatched to RabbitMQ<br/>(payment-events fanout exchange)
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

    Admin->>EP: POST /api/v1/payments/process<br/>{ OrderId, Amount, Currency }
    Note over EP: BuyerId comes from the JWT<br/>NameIdentifier claim, never the body<br/>(mass-assignment guard)
    EP->>Bus: bus.InvokeAsync<Guid>(command, ct)
    Bus->>H: same Acceptor as Flow 1
    Note over H: idempotency check + persist Pending +<br/>publish PaymentProcessingRequested<br/>(identical to Flow 1 — the gateway call<br/>happens later on the bus)
    H-->>Bus: payment.Id
    Bus-->>EP: payment.Id
    EP-->>Admin: 202 Accepted + { Id }<br/>Location: /api/v1/payments/{id}
```

The admin endpoint matters because it's how an operator manually triggers a payment when the saga is unavailable (e.g. RabbitMQ outage). Same code path; same outbox guarantees.

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
    participant Out as IDbContextOutbox<br/>(Wolverine non-handler outbox)
    participant DB as SQL Server +<br/>wolverine.outgoing_envelopes
    participant MQ as RabbitMQ<br/>(payment-events fanout exchange)

    loop every SweepInterval (default 60s)
        Tick->>Lock: TryAcquireLockAsync("payments-recovery",<br/>TimeSpan.Zero)
        alt another replica holds it
            Lock-->>Tick: null → skip this tick
        else acquired
            Tick->>Scope: create fresh DI scope<br/>(NOT reused across iterations —<br/>change-tracker stays small)
            Scope-->>Tick: PaymentDbContext + IDbContextOutbox
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
                    Note over Tick,DB: NON-HANDLER OUTBOX —<br/>BackgroundService runs OUTSIDE<br/>Wolverine's handler pipeline so<br/>AutoApplyTransactions does NOT apply<br/>here — enroll the DbContext manually.
                    Tick->>Out: outbox.Enroll(context)
                    Tick->>Agg: MarkAsFailed("timed out — recovery sweep")
                    Tick->>Out: outbox.PublishAsync(PaymentFailedEvent)
                    Note over Out: stages the outbox envelope<br/>(not yet persisted)
                    Tick->>Out: outbox.SaveChangesAndFlushMessagesAsync(ct)
                    Note over Ctx,DB: saves BOTH the MarkAsFailed mutation<br/>AND the staged envelope in one tx,<br/>then flushes to the broker
                    DB-->>Tick: tx commit (both rows or neither)
                    DB->>MQ: PaymentFailedEvent dispatched
                end

                alt DbUpdateConcurrencyException
                    Note over Tick: another process won the<br/>RowVersion race. Roll back —<br/>no further action needed.
                end
            end
        end
    end
```

**The outbox-outside-handler trap.** Wolverine's `AutoApplyTransactions` policy wraps **handler chains** — anything dispatched through `IMessageBus.InvokeAsync`/`PublishAsync` into a handler gets the wrap automatically. That includes the admin path in Flow 2 (`POST /payments/process` → `bus.InvokeAsync<Guid>(command)` → `ProcessPaymentHandler`) — it's still inside the Wolverine pipeline, so the wrap applies. The trap is code that publishes events **without entering a handler at all**: `BackgroundService` sweep loops, cron jobs, or any future code path that does `bus.PublishAsync(@event)` outside an active Wolverine handler context. Outside a handler, a plain publish is not enlisted in any transaction, so the entity write and the envelope can come apart. The canonical safe wrap is Wolverine's **non-handler outbox**, `IDbContextOutbox` (inline in `PaymentRecoveryJob.RecoverOneAsync`, no longer behind a repository method):

```csharp
outbox.Enroll(context);
// entity work...
payment.MarkAsFailed(reason);
await outbox.PublishAsync(new PaymentFailedEvent { ... });
await outbox.SaveChangesAndFlushMessagesAsync(ct);   // saves entity mutation + staged envelope in one tx, then flushes
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

**Idempotency by throw (not no-op).** Same pattern as OrderService's `Order.MarkAsX` methods: `Payment.MarkAsCompleted` and `MarkAsFailed` **throw `InvalidOperationException`** if status is not `Pending` — the aggregate is the invariant guard; the handler's pre-check is the idempotency layer, in both services. The reason: PaymentService's idempotency lives at the *handler* level (existence check on `OrderId`), not the aggregate level. By the time you're calling `MarkAsX` here, you've already loaded a freshly-created `Pending` payment in the current request — a non-Pending status means a serious bug, not a duplicate event.

---

## File inventory

| Path | Purpose |
|---|---|
| [Endpoints/PaymentEndpoints.cs](../../PaymentService/Endpoints/PaymentEndpoints.cs) | HTTP admin path: `POST /api/v1/payments/process` |
| [Features/OrderPlacedHandler.cs](../../PaymentService/Features/OrderPlacedHandler.cs) | Static event translator (Wolverine cascading message) |
| [Features/ProcessPayment.cs](../../PaymentService/Features/ProcessPayment.cs) | Command + validator + handler (idempotency + gateway + state + publish) |
| [Domain/Payment.cs](../../PaymentService/Domain/Payment.cs) | Aggregate root + state guards (throw on bad transition) |
| [Domain/PaymentStatus.cs](../../PaymentService/Domain/PaymentStatus.cs) | Enum: Pending / Completed / Failed / Refunded (Refunded is reserved — no transition drives it yet) |
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
- [docs/transactional-outbox.svg](../transactional-outbox.svg) — diagram of outbox mechanics; the recovery job's `IDbContextOutbox` `Enroll` → `PublishAsync` → `SaveChangesAndFlushMessagesAsync` is the non-handler variant of the same pattern
- [docs/performance-and-data-correctness.md](../performance-and-data-correctness.md) — full perf rationale incl. outbox + sweeper patterns
- [docs/event-catalog.md](../event-catalog.md) — every event's shape and producer/consumer
