# War story: the Wolverine 5→6 upgrade that silently broke the transactional outbox

> A routine major-version bump turned a load-bearing correctness guarantee — the transactional
> outbox — into a happy-path illusion. Every existing test stayed green. The bug only showed up
> when we forced a database commit to fail. This is the full arc: the symptom, the wrong turns,
> the non-obvious root cause, the one-line fix, and what a senior dev would notice versus what a
> junior dev should take away.

**Audience:** anyone using a message framework with a transactional outbox (Wolverine, MassTransit,
NServiceBus, Brighter, or a hand-rolled one). The specific API names are Wolverine's; the *failure
mode* is universal.

---

## TL;DR

We upgraded `WolverineFx` from 5.39.3 to 6.8.0. Three breaking changes surfaced. The first two were
easy (a NuGet package split, a default policy flip). The third was nasty:

**In Wolverine 5.x, publishing an event through a *constructor-injected* `IMessageBus` (wrapped in our
`IEventPublisher` shim) enlisted that publish in the handler's database transaction — so the event was
*staged* in the outbox and only dispatched after the entity write committed. In 6.x it does not.** A
publish through the constructor-injected bus now fires *immediately*, before `SaveChanges` commits.

Result: the transactional outbox — the thing that guarantees "the entity write and the event publish
commit together, or not at all" — was silently defeated across every service. The fix is one word:
publish through the `IMessageContext` Wolverine injects as a **handler method parameter**, not the one
resolved from the constructor. The wrong fixes we tried first were all bigger than the right one.

---

## 1. The setup

NextAurora is an event-driven saga: `OrderPlaced → PaymentCompleted → ShipmentDispatched → notify`.
Each step is a handler that (a) writes to its own database and (b) publishes an event the next service
consumes. The contract that makes this safe under crashes is the **transactional outbox**:

> The event isn't sent to the broker directly. It's written into a `wolverine_outgoing_envelopes`
> table **in the same database transaction as the entity write**. A background dispatcher delivers it
> *after* the transaction commits. So either both the entity and the event persist, or neither does.
> No "order saved but event lost," and no "event sent but order rolled back."

Every service published events through a thin shim:

```csharp
public sealed class WolverineEventPublisher(IMessageBus bus) : IEventPublisher
{
    public Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : class
        => bus.PublishAsync(@event).AsTask();
}
```

Handlers depended on `IEventPublisher`, called `PublishAsync` before `SaveChanges`, and trusted the
outbox to make the two atomic. This worked for the entire life of the codebase — on Wolverine 5.

The upgrade itself looked boring: bump six `WolverineFx.*` packages, `dotnet restore`, `dotnet build`.
Zero compile errors. 116 unit + architecture tests green. That's the trap — **a major-version bump can
be 100% source-compatible and still change runtime semantics.**

---

## 2. The symptom

Only the integration tests caught anything, and only two of them: PaymentService's Acceptor→Gateway
flow. PaymentService deliberately splits payment processing into two handlers:

- **Acceptor** (`ProcessPaymentHandler`): on `POST /payments/process`, persist `Payment(Pending)` and
  publish a **local** `PaymentProcessingRequested` continuation, then return `202` fast.
- **Gateway** (`PaymentProcessingRequestedHandler`): consume `PaymentProcessingRequested`, call the
  payment provider (seconds-slow), mark the payment `Completed`, publish `PaymentCompletedEvent`.

Under 6.x the payment was stuck in `Pending` forever, and the test failed with:

```
System.Exception : No messages of type PaymentCompletedEvent were received
```

The first instinct — "the event tracking changed" — was wrong, and chasing it wasted time. The real
question was: **why is the payment never completed?**

---

## 3. The wrong turns (this is where the learning is)

A good war story isn't the clean final fix; it's the hypotheses that *felt* right and weren't.

### Wrong turn #1 — "the local queue isn't durable anymore"

Wolverine 6 split local-queue durability into its own policy. Plausible! We added
`opts.Policies.UseDurableLocalQueues()`. **No change** — payment still `Pending`.

### Wrong turn #2 — "swap the shim to `IMessageContext`"

`IMessageContext` is Wolverine's transaction-aware messaging context, a superset of `IMessageBus`.
Surely injecting *that* into the shim fixes enlistment? We changed
`WolverineEventPublisher(IMessageBus)` → `WolverineEventPublisher(IMessageContext)`. **No change.**
(This wrong turn hid the real answer in plain sight — see §5.)

### Wrong turn #3 — "both together"

Durable local queues *and* the context swap. **Still no change.** Three plausible fixes, zero progress
— a strong signal we didn't understand the mechanism yet.

### The red herring — a stale test assembly

At one point a fix "didn't work"… because we'd rebuilt the *service* project but run the tests with
`--no-build` against a **stale test assembly**. Lesson: when a result makes no sense, suspect your
build/measurement before your hypothesis. (We caught it because the error text referenced a diagnostic
that the current source no longer contained.)

### The dead-end signal — the logs lie by omission

We tried to *observe* staging cheaply: grep the EF command log for `INSERT INTO wolverine_outgoing_envelopes`.
There was none — looked like proof of "not staged." But then we checked the *known-good* staged path
(after the eventual fix) and **it also showed no envelope INSERT**. Wolverine stages envelopes through
a path the EF command logger doesn't surface (raw ADO on the shared connection). **An absent signal
that's absent in both the broken and the working case proves nothing.** We threw the shortcut away and
went looking for a *conclusive* signal.

---

## 4. The breakthrough — watch the transaction boundary

The conclusive signal was ordering. With detailed SQL logging on the Acceptor→Gateway flow:

```
Starting to process ProcessPaymentCommand        ← Acceptor begins
Starting to process PaymentProcessingRequested    ← Gateway begins … BEFORE the next line
INSERT INTO [Payments] (...)                       ← Acceptor finally writes Payment(Pending)
```

The Gateway handler **ran before the Acceptor's `INSERT`**. So when the Gateway did
`context.Payments.FirstOrDefaultAsync(p => p.Id == message.PaymentId)`, the row didn't exist yet — it
returned `null`, the handler no-op'd (correctly guarding against a missing row), and the Acceptor then
committed `Pending` afterward. Stuck forever.

That can only happen if the continuation was **published inline** — dispatched the instant
`PublishAsync` was called, *not* held in the outbox until the Acceptor committed. The whole point of
the outbox is to not do that.

### "Is it the test harness or is it real?"

A fair objection: the test uses `TrackActivity().ExecuteAndWaitAsync(...)`, which might force inline
cascade delivery. So we removed the harness entirely — plain `await client.PostAsJsonAsync(...)` then
poll the database for 15 seconds. **Still `Pending`.** Not a harness artifact. Real behavior, and
therefore a real production bug.

---

## 5. The root cause — constructor injection vs *method* injection

Here's the non-obvious part, and the reason wrong-turn #2 failed.

Wolverine runs each handler inside a per-message **`IMessageContext`** that is enlisted in the outbox
transaction. Messages published through *that* context are staged and committed atomically. **But you
only get the enlisted context if Wolverine hands it to you — as a `HandleAsync` method parameter.**

```csharp
// ENLISTED — Wolverine injects the message-scoped, transaction-aware context here:
public async Task HandleAsync(PlaceOrderCommand cmd, IMessageContext messageContext, CancellationToken ct)

// NOT ENLISTED — a context/bus resolved from the DI container (what IEventPublisher wraps):
public PlaceOrderHandler(IEventPublisher publisher)  // publisher → IMessageBus → a DIFFERENT instance
```

A **constructor-injected** `IMessageBus` *or* `IMessageContext` is **not** the message-scoped enlisted
one. That's why swapping the shim's constructor type (`IMessageBus` → `IMessageContext`) in wrong-turn
#2 changed nothing: it was still constructor-injected, still not the enlisted context, still inline.

Why did 5.x work? In 5.x the constructor-injected publish path *was* effectively enlisted in the
ambient handler transaction. 6.x tightened the model: enlistment now flows **only** through the
context Wolverine injects into the handler signature. The `IEventPublisher`-over-constructor-`IMessageBus`
pattern — which had been the project's blessed way to publish — became silently non-transactional.

> **Senior-dev takeaway:** in message frameworks, *where the messaging context comes from* (framework-
> injected per-message vs container-resolved) determines whether it participates in the unit of work.
> "It implements `IMessageBus`, so it'll behave the same" is exactly the assumption that bites you.

---

## 6. The bigger discovery — it wasn't just the local continuation

PaymentService's local cascade is what *failed a test*, because the Gateway reads back the row the
Acceptor wrote — a same-request data dependency. But the same shim published **external** events too:
`OrderPlacedEvent`, `PaymentCompletedEvent`, `ShipmentDispatchedEvent`. Those had no same-request
reader, so no test failed — but were they still atomic?

We refused to assume. We wrote the one test that proves it, the only conclusive way: **force the commit
to roll back after the publish, and assert the event was not dispatched.**

```csharp
// A SaveChanges interceptor that throws when an Order is being committed — simulating a
// crash/constraint at commit, AFTER the handler has already called PublishAsync.
public sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var committingNewOrder = eventData.Context?.ChangeTracker
            .Entries<Order>().Any(e => e.State == EntityState.Added) ?? false;
        if (committingNewOrder) throw new InvalidOperationException("Simulated commit failure after publish.");
        return base.SavingChangesAsync(eventData, result, ct);
    }
}
```

```csharp
// Two assertions. The first is also a guard: if the interceptor silently didn't apply, an order
// WOULD persist and this fails loudly — so the second assertion can never give a false pass.
orphanExists.Should().BeFalse("the commit rolled back, so no Order row may persist");
session.Sent.MessagesOf<OrderPlacedEvent>().Should().BeEmpty(
    "a rolled-back commit must not leave an OrderPlacedEvent dispatched");
```

Result on the unfixed code: the order rolled back (first assertion passed) **but `OrderPlacedEvent` was
already dispatched** (second assertion failed). External publishes were non-atomic too. The upgrade had
silently broken the outbox across the whole system.

### Why this matters in production (not a local-dev quirk)

This bug is **transport-agnostic** — it's on the database/outbox side, so it behaves identically against
a local emulator, real Azure Service Bus, or RabbitMQ. In production:

1. `PlaceOrder` publishes `OrderPlacedEvent` → it goes to the real broker **immediately**.
2. `SaveChanges` then fails (constraint, optimistic-concurrency conflict, transient blip, pod killed).
3. The order **never persists** — but the event is already on the bus.
4. PaymentService consumes it and **charges a customer for an order that doesn't exist.**

Intermittent (only in the commit-failure window), invisible to happy-path tests, and money-touching.
The exact class of bug the outbox exists to prevent.

---

## 7. The fix

One method parameter per affected handler. Publish the event through the enlisted `IMessageContext`:

```csharp
// before
public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken ct)
{
    ...
    await eventPublisher.PublishAsync(orderPlaced, ct);   // constructor shim — NOT enlisted
    await context.SaveChangesAsync(ct);
}

// after
public async Task<Guid> HandleAsync(PlaceOrderCommand request, IMessageContext messageContext, CancellationToken ct)
{
    ...
    await messageContext.PublishAsync(orderPlaced);       // method-injected — enlisted & staged
    await context.SaveChangesAsync(ct);                   // flushes entity + envelope in ONE transaction
}
```

Applied to every handler that writes-then-publishes: `PlaceOrder`, `CreateShipment`, and PaymentService's
Gateway. The rollback test flipped from red to green. The `IEventPublisher` shim, now unused by
OrderService and ShippingService handlers, was deleted (dead code — the codebase's rule is to remove
ports that have no consumer). It's retained only in PaymentService, for two paths that *aren't*
write-then-publish handlers (see below).

> **Notice the asymmetry that took the longest to see:** the four heavy hypotheses (durable local
> queues, constructor-context swap, both, plus harness suspicion) were all wrong, and the correct fix
> was the *smallest* possible change — one parameter. When the right fix is smaller than the wrong ones,
> it usually means you finally understand the mechanism instead of pattern-matching around it.

---

## 8. What we deliberately did *not* change (and why)

- **The Acceptor's "republish" path** still uses the constructor `IEventPublisher`. It re-publishes a
  *terminal* event for an **already-committed** payment (the idempotency/redelivery path). There's no
  entity write to be atomic *with*, and the intent is to send *immediately*. Inline publish is correct
  here — forcing it through a staged context would be wrong (it would never flush without a `SaveChanges`).
  **The same API is right or wrong depending on whether there's a unit of work to join.**
- **`PaymentRecoveryJob`** (a background sweeper that marks timed-out payments `Failed` and publishes
  `PaymentFailedEvent`) is a *non-handler*: it has no method-injected context, and uses an explicit
  `BeginTransactionAsync`. It has the same latent non-enlistment, but the correct fix is different —
  Wolverine's non-handler outbox API (`IDbContextOutbox` / `IMessageContext.EnlistInOutboxAsync`) — and
  it deserves its own "outbox-in-non-handler" rollback test (which didn't exist). We scoped it out to a
  tracked follow-up rather than rush a money-path change. **Knowing where to stop is part of the fix.**

---

## 9. Lessons

**For the junior dev:**

- **A major-version upgrade can compile cleanly and still change behavior.** "It builds and the tests
  pass" is necessary, not sufficient. Read the release notes for *semantic* changes, not just API ones.
- **The transactional outbox, in one sentence:** stage the event in the same DB transaction as the
  entity, deliver it after commit — so a crash can't leave the two out of sync. If your publish isn't
  part of that transaction, you don't have an outbox; you have hope.
- **Constructor injection ≠ method injection in a message handler.** The framework-injected per-message
  context is the one wired into the unit of work; a container-resolved one usually isn't.
- **Prove correctness with a failure test.** "Atomicity" is only demonstrated by making the commit fail
  and checking nothing leaked. A passing happy-path test says nothing about the rollback path.
- **The smallest fix that addresses the root cause beats a pile of plausible config.** If your fix keeps
  growing, you probably don't understand the cause yet.

**For the senior dev:**

- **An absent signal in both the broken and working case is not evidence.** We almost concluded from
  "no envelope INSERT in the log," until we checked the known-good path and saw the same absence.
  Validate your instrument against a control before trusting it.
- **Distinguish "the test harness did it" from "the system does it."** Re-running without the harness
  (plain HTTP + DB poll) is what turned a maybe-test-artifact into a confirmed production bug.
- **The guard-assertion pattern:** the rollback test's first assertion (`no order persisted`) also
  proves the interceptor fired, so the load-bearing second assertion can't give a false pass. Design
  tests so a mis-wired harness fails loudly instead of passing quietly.
- **Same API, opposite correctness, by context.** Inline publish is a bug in the write-then-publish
  handler and *correct* in the already-committed republish path. Resist blanket find-and-replace.
- **Scope discipline on the critical path.** Fixing the handlers (proven) and deferring the non-handler
  recovery job (different mechanism, needs its own test) is a deliberate, documented stop — not an
  oversight.

---

## Appendix — the other two breaking changes (for completeness)

1. **Runtime code generator split out of core (GH-2876).** Wolverine 6 no longer ships the Roslyn
   compiler in `WolverineFx`; in the default `TypeLoadMode.Dynamic` the host throws at startup. Fix:
   reference `WolverineFx.RuntimeCompilation` (auto-registers). Production-grade alternative: pre-generated
   static codegen (`TypeLoadMode.Static`).
2. **`ServiceLocationPolicy` default → `NotAllowed`.** Generated handler code that must resolve a
   non-inlinable dependency from the container (e.g. an interface with a factory registration) now fails
   the build by default. Fix: `opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed`. This is
   a codegen-strategy knob, not the service-locator anti-pattern.

See also: [docs/project-decisions.md](project-decisions.md) §13 "Wolverine 5→6 upgrade notes", and the
canonical rule in [CLAUDE.md](../CLAUDE.md) ("In-handler transactional publishing needs a method-injected
`IMessageContext`").
