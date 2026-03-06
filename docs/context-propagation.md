# Context Propagation: CorrelationId, UserId, and SessionId

## The Problem It Solves

Imagine a user places an order. That single action kicks off a chain of work across five different services:

```
Storefront → OrderService → PaymentService → ShippingService → NotificationService
```

Each service writes its own log files. When something goes wrong in PaymentService, you have a pile of log lines from every service — but no way to know which ones belong to *that specific user's* failed order. You'd be sifting through thousands of unrelated entries.

Context propagation solves this by stamping **every log line** in every service with three identifiers that travel with the request through the entire chain:

| Field | What It Is | Example |
|-------|-----------|---------|
| `CorrelationId` | A unique ID for the whole transaction | `a1b2c3d4e5f6` |
| `UserId` | The authenticated user's ID | `user-987` |
| `SessionId` | The browser/app session | `sess-abc123` |

With these in your logs, you can search for `CorrelationId = "a1b2c3d4e5f6"` and see exactly what every service did for that one transaction — nothing more, nothing less.

---

## What a Log Line Looks Like

Without context propagation:
```
INFO  Processing payment for order abc
ERROR Payment gateway timeout
```

With context propagation:
```json
{
  "timestamp": "2026-03-06T03:00:00.000Z",
  "level": "ERROR",
  "CorrelationId": "a1b2c3d4e5f6",
  "UserId": "user-987",
  "SessionId": "sess-abc123",
  "message": "Payment gateway timeout"
}
```

Now you can instantly find every log line for this user's transaction, across all services, in the right order.

---

## How the Three IDs Get Their Values

```
Browser / App Client
      │
      │  HTTP Request
      │  Headers:
      │    X-Correlation-Id: a1b2c3d4e5f6   (client sends, or server generates one)
      │    X-Session-Id:     sess-abc123     (client generates once per session)
      │    Authorization:    Bearer <JWT>    (contains the user's ID)
      │
      ▼
   API Gateway / Service HTTP Endpoint
      │
      │  CorrelationIdMiddleware runs here
      │  - Reads X-Correlation-Id (generates one if missing)
      │  - Reads UserId from JWT claim
      │  - Reads SessionId from X-Session-Id header
      │  - Stores all three in Activity baggage
      │
      ▼
   Wolverine Handler (via ContextPropagationMiddleware)
      │
      │  ContextPropagationMiddleware reads baggage, opens logger scope
      │  → Every log line in the handler now carries all three IDs
      │
      ▼
   WolverineEventPublisher (outgoing async message)
      │
      │  Reads baggage, writes to message ApplicationProperties:
      │    X-Correlation-Id, X-User-Id, X-Session-Id
      │
      ▼
   Next Service's Processor (incoming async message)
         │
         │  Reads ApplicationProperties back into Activity baggage
         │  Opens logger scope — same three IDs continue
         │
         ▼
      And so on...
```

---

## The Moving Parts

### 1. `CorrelationIdMiddleware` — The Entry Point for HTTP

**File:** `NextAurora.ServiceDefaults/Middleware/CorrelationIdMiddleware.cs`

This runs on every incoming HTTP request before anything else. It:

1. Looks for `X-Correlation-Id` in the request headers. If not found, generates a new ID from the trace.
2. Reads the `sub` claim from the JWT bearer token (the authenticated user's ID).
3. Reads `X-Session-Id` from the request headers.
4. Stores all three in **Activity baggage** — think of this as a thread-local backpack that travels with the request.
5. Opens a `logger.BeginScope()` so every log line within this request automatically includes all three fields.
6. Echoes `X-Correlation-Id` back in the response header so the client can use it for support requests.

```csharp
// Simplified view of what it does
var correlationId = request.Headers["X-Correlation-Id"] ?? GenerateNew();
var userId        = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;   // from JWT
var sessionId     = request.Headers["X-Session-Id"].FirstOrDefault();

Activity.Current?.SetBaggage("correlation.id", correlationId);
if (userId    is not null) Activity.Current?.SetBaggage("user.id",     userId);
if (sessionId is not null) Activity.Current?.SetBaggage("session.id",  sessionId);
```

> **Note:** `UserId` is only present when the user is authenticated. Anonymous requests will have `CorrelationId` and possibly `SessionId`, but not `UserId`. This is intentional — never crash because a field is absent.

---

### 2. `ContextPropagationMiddleware` — Enriches Every Handler Log Line

**File:** `NextAurora.ServiceDefaults/Messaging/ContextPropagationMiddleware.cs`

Wolverine's middleware pipeline lets you run cross-cutting code around every command and query handler. `ContextPropagationMiddleware` sits in that pipeline and:

1. Reads the three IDs from `Activity.Current` baggage.
2. Opens a `logger.BeginScope()` for the duration of the handler.

Because of the `BeginScope`, **every single log line** produced anywhere inside the handler — including lines from repositories, domain services, or anything else called transitively — automatically carries `CorrelationId`, `UserId`, and `SessionId` without those classes needing to know about them.

```
Request arrives
    └── FluentValidation (validates the command)
            └── ContextPropagationMiddleware (opens scope with CorrelationId/UserId/SessionId)
                    └── YourHandler.Handle()         ← all logs here carry the IDs
                            └── Repository.SaveAsync()  ← and here too
```

---

### 3. `WolverineEventPublisher` — Carries Context to the Next Service

**Files:** `{Service}.Infrastructure/Messaging/WolverineEventPublisher.cs`

When a service publishes an event, context would normally be lost — the message is fire-and-forget. `OutgoingContextMiddleware` (also in `ServiceDefaults`) runs on every outgoing Wolverine envelope and copies the baggage into the envelope's `ApplicationProperties`:

```csharp
// Written onto every outgoing Service Bus message
message.ApplicationProperties["X-Correlation-Id"] = correlationId;
if (userId    is not null) message.ApplicationProperties["X-User-Id"]     = userId;
if (sessionId is not null) message.ApplicationProperties["X-Session-Id"]  = sessionId;
```

These properties ride along with the message body and are available to the receiving service.

---

### 4. Wolverine Message Handlers — The Entry Point for Async Messages

Wolverine discovers and invokes handler methods (`Handle(TEvent e)`) automatically for every incoming Service Bus message. `ContextPropagationMiddleware` runs before each handler, mirroring what `CorrelationIdMiddleware` does for HTTP — it reads `ApplicationProperties` from the Wolverine `Envelope` and restores all three IDs into Activity baggage and a logger scope.

The properties extracted from each message:

```csharp
// Extracted from the message, not an HTTP request
var correlationId = message.ApplicationProperties["X-Correlation-Id"]?.ToString();
var userId        = message.ApplicationProperties["X-User-Id"]?.ToString();
var sessionId     = message.ApplicationProperties["X-Session-Id"]?.ToString();

// Put into baggage so downstream code can read it
Activity.Current?.SetBaggage("correlation.id", correlationId);
if (userId    is not null) Activity.Current?.SetBaggage("user.id",    userId);
if (sessionId is not null) Activity.Current?.SetBaggage("session.id", sessionId);

// The middleware handles this automatically — no per-handler boilerplate needed
// ContextPropagationMiddleware.Before() runs before every Wolverine handler
```

All three IDs are available in Activity baggage for the duration of the handler chain.

---

## The Baggage / Scope Key Names

These names are the contract. Use them exactly — casing matters.

| Concept | Activity Baggage Key | Message Property | Logger Scope Key |
|---------|---------------------|-----------------|-----------------|
| Correlation ID | `correlation.id` | `X-Correlation-Id` | `CorrelationId` |
| User ID | `user.id` | `X-User-Id` | `UserId` |
| Session ID | `session.id` | `X-Session-Id` | `SessionId` |

**Baggage keys** use dots (lowercase) — this matches the OpenTelemetry W3C baggage standard.  
**Message/HTTP property names** use `X-` prefix with title case — standard HTTP header convention.  
**Logger scope keys** use PascalCase — this is what appears in your structured log output (Seq, Application Insights, etc.).

---

## Adding Context Propagation to a New Service

If you add a fifth or sixth service, here is the checklist:

**HTTP entry point:**
- `CorrelationIdMiddleware` runs automatically via `app.MapDefaultEndpoints()` — no extra registration needed per service.

**Wolverine pipeline:**
- Call `opts.AddNextAuroraContextPropagation()` inside `UseWolverine()` — it's in `ServiceDefaults` and registers both `ContextPropagationMiddleware` and `OutgoingContextMiddleware`.

**Outgoing events:**
- Register `WolverineEventPublisher` as `IEventPublisher` in Infrastructure DI. `OutgoingContextMiddleware` stamps the three IDs onto every outgoing envelope automatically.

**Incoming Service Bus messages:**
- Wolverine + `ContextPropagationMiddleware` handles context extraction for all async message handlers automatically. No per-handler boilerplate needed.

---

## Searching Logs in Practice

Once deployed with a structured log sink (Seq, Azure Monitor, Application Insights), you can:

**Find everything for one transaction:**
```
CorrelationId = "a1b2c3d4e5f6"
```

**Find all errors for a specific user today:**
```
UserId = "user-987" AND level = "ERROR" AND timestamp > "2026-03-06"
```

**Find all actions in a browser session:**
```
SessionId = "sess-abc123"
```

**Reconstruct the full event chain for a failed order:**
```
CorrelationId = "a1b2c3d4e5f6" | sort by timestamp asc
```

This last query gives you every log line — from the initial HTTP request through every Service Bus hop — in chronological order, across all five services.

---

## Common Pitfalls

**Don't null-check aggressively in handlers.** The context is already null-safe at the middleware and processor level. If `UserId` is absent from baggage, it simply won't appear in the log scope — your handler doesn't need to worry about it.

**Don't create a new `CorrelationId` inside a handler.** It should always flow from the entry point. If you generate a new one mid-chain, you'll break the trail and be unable to join log lines across services.

**Don't use `string.Empty` as a fallback.** An empty string will appear as a log field with no value, which is more confusing than the field simply being absent. The null-check pattern (`if (x is not null) scope["Key"] = x`) is deliberate.

**Dictionary requires `StringComparer.Ordinal`.** The Meziantou analyzer (MA0002) enforces this. Always pass the comparer:
```csharp
var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
```

**Activity may be null.** In test environments without a tracing listener, `Activity.Current` can be null. All baggage reads use `?.` to guard against this.
