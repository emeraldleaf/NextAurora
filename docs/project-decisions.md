# Project Decisions — API, Libraries, Architecture

This is the reference companion to [docs/ef-core.md](ef-core.md). It walks every cross-cutting decision in NextAurora — *why* this stack, *why* this API style, *what trade-offs were accepted*. Every section ties back to the canonical rules in [CLAUDE.md](../CLAUDE.md); the perf-specific decisions live in [performance-and-data-correctness.md](performance-and-data-correctness.md).

## Table of Contents

- [1. Overview — what this doc covers](#1-overview--what-this-doc-covers)
- [2. Architectural style — microservices over modular monolith](#2-architectural-style--microservices-over-modular-monolith)
- [3. Per-service Clean Architecture (4 layers)](#3-per-service-clean-architecture-4-layers)
- [4. API: Minimal APIs, not Controllers](#4-api-minimal-apis-not-controllers)
- [5. URL-segment versioning (`/api/v1/...`)](#5-url-segment-versioning-apiv1)
- [6. OpenAPI + YAML + Scalar UI](#6-openapi--yaml--scalar-ui)
- [7. Authentication — Keycloak + JWT Bearer](#7-authentication--keycloak--jwt-bearer)
- [8. Authorization — `.RequireAuthorization()` + buyer-scope checks](#8-authorization--requireauthorization--buyer-scope-checks)
- [9. Validation — FluentValidation via Wolverine policy](#9-validation--fluentvalidation-via-wolverine-policy)
- [10. Error handling — `GlobalExceptionHandler` + RFC 7807](#10-error-handling--globalexceptionhandler--rfc-7807)
- [11. Rate limiting](#11-rate-limiting)
- [12. Communication patterns — REST / gRPC / Service Bus](#12-communication-patterns--rest--grpc--service-bus)
- [13. Wolverine — covers MediatR + MassTransit, both now commercial](#13-wolverine--covers-mediatr--masstransit-both-now-commercial)
- [14. Observability — OpenTelemetry + context propagation](#14-observability--opentelemetry--context-propagation)
- [15. Logging — `Microsoft.Extensions.Logging`, not Serilog](#15-logging--microsoftextensionslogging-not-serilog)
- [16. HybridCache — chosen over hand-rolled L1/L2](#16-hybridcache--chosen-over-hand-rolled-l1l2)
- [17. Resilience — `Microsoft.Extensions.Http.Resilience` (Polly v8)](#17-resilience--microsoftextensionshttpresilience-polly-v8)
- [18. Testing — xUnit + AwesomeAssertions + NSubstitute + Testcontainers](#18-testing--xunit--awesomeassertions--nsubstitute--testcontainers)
- [19. Build system & static analysis](#19-build-system--static-analysis)
- [20. Library decisions reference table](#20-library-decisions-reference-table)
- [21. Crib sheet](#21-crib-sheet)
- [22. Dapr — considered, not adopted (and distributed locks)](#22-dapr--considered-not-adopted-and-distributed-locks)

---

## 1. Overview — what this doc covers

NextAurora is a .NET 10 / C# 13 microservices platform. The decisions in this doc are the cross-cutting ones — picks that apply to every service, not just to one bounded context.

**Where the rules live:**
- Hard rules every PR must follow → [CLAUDE.md](../CLAUDE.md) ("Performance Rules", "Coding Standards", "Communication Patterns", "Key Conventions", "Security Requirements")
- Why each rule exists → [performance-and-data-correctness.md](performance-and-data-correctness.md)
- How EF Core is used specifically → [ef-core.md](ef-core.md)
- Cross-session state → [STATUS.md](STATUS.md)

This doc is the **map of the technical decisions** with the *rationale* for each. For a quick deep-dive, sections 4, 12, 13, 16, and the library table are the most discussion-rich.

---

## 2. Architectural style — microservices over modular monolith

NextAurora is **microservices, not modular monolith**. Five backend services, each independently deployable, each owning its own database, each communicating with peers via gRPC (sync) or RabbitMQ (async, via Wolverine).

```
NextAurora/
  CatalogService/        # Postgres, REST + gRPC server
  OrderService/          # SQL Server
  PaymentService/        # SQL Server
  ShippingService/       # Postgres
  NotificationService/   # stateless

  NextAurora.AppHost/    # Aspire orchestrator
  NextAurora.ServiceDefaults/   # cross-cutting infra (auth, OTel, exception handler, versioning)
  NextAurora.Contracts/         # cross-service event/DTO contracts
```

### Why microservices and not modular monolith

Milan Jovanović's book *Modular Monolith Architecture* (which this project shares many tactical patterns with — see the [comparison](#21-crib-sheet)) argues "**always start with at least schema-level data isolation in one process**." Our shape goes further — separate processes, separate databases per service, separate deployment artifacts.

**Honest answer:** for a *real* greenfield production system, his recommendation is correct. We picked microservices-from-day-one because the project's *purpose* is demonstrating distributed-system patterns in their natural habitat:

- The saga choreography (Order → Payment → Shipping → Notification) genuinely runs across processes
- The transactional outbox solves a *real* dual-write problem, not a hypothetical one
- gRPC inter-service calls are real network hops, not in-process method dispatches
- The concurrency tokens defend against real concurrent updates across services

For a learning/portfolio project where the *patterns themselves* are the deliverable, the microservices shape is the point. For real production at the size of any one team starting fresh today, modular monolith is usually the right call.

### Trade-offs we accepted

| Cost | What it buys |
|---|---|
| 5× the deployment surface | Independent scaling per service |
| Real network calls (gRPC, Service Bus) | True data autonomy per bounded context |
| Cross-service eventual consistency | No shared DB schema → bounded contexts can evolve independently |
| Polyglot persistence (Postgres + SQL Server) | Provider chosen per workload |
| Saga complexity | Failures of one service don't crash others |

---

## 3. Per-service shape: Clean Architecture *or* Vertical Slice Architecture

**Originally**: every service used the same 4-project Clean Architecture split. **Now**: only
CatalogService keeps that shape. The other four services collapsed to single-project Vertical
Slice Architecture (VSA) after an audit found the layered split was costing more than it paid
at their size.

### CatalogService — Clean Architecture (4 projects)

```
CatalogService/
  CatalogService.Domain/         # Entities, value objects, enums, repository interfaces — NO dependencies
  CatalogService.Application/    # Commands, queries, handlers, validators, mappers — depends on Domain
  CatalogService.Infrastructure/ # EF Core, repositories, caching, external gateways — depends on Domain + Application
  CatalogService.Api/            # Minimal API endpoints, gRPC services, DI wiring — composition root
```

Enforced by **project references** at compile time:

```
Domain          →  (nothing)
Application     →  Domain
Infrastructure  →  Domain + Application
Api             →  all layers
```

Why CatalogService kept this shape: multiple aggregates (Product + Category), two-tier
HybridCache, gRPC server, optimistic concurrency, integration tests. Enough cross-cutting
concerns that compile-time layer enforcement protects against real violations.

### Order / Payment / Shipping / Notification — VSA (1 project)

```
{Service}/
  Features/                # One file per use case (command/query + handler co-located).
                          # Saga event handlers live here too — they own real state machines.
  Domain/                  # Aggregates, value objects, ports (interfaces consumed by features).
  Infrastructure/          # EF Core (Data/ + Migrations/), repositories, gateways, DI.
  Endpoints/               # Minimal-API HTTP surface.
  Program.cs               # Composition root.
  {Service}.csproj         # Single Web SDK project.
```

Why VSA for these: each is ≤2 aggregates and 250–1400 LOC. The four-project ceremony
(separate csprojs, cross-project `using`s, "find everything related to PlaceOrder is a
multi-project search") was *taller* than the protection it was offering. VSA collapses the
internal split: one project, feature folders, Domain folder for what's genuinely shared,
discipline doing the work compile-time references used to do.

### Why two shapes coexists by design

The Clean-Arch–vs–VSA decision is per-service, not per-project. The signal isn't "we prefer
one pattern" — it's "this service is big enough to earn the layer boundaries / this one
isn't." See [CLAUDE.md "Project Structure"](../CLAUDE.md#project-structure) for the decision
table and [CLAUDE.md "Interfaces earn their keep through consumer substitution"](../CLAUDE.md#solid)
for why the four VSA services *still* keep their `IFooRepository` / `IEventPublisher` /
`IPaymentGateway` ports despite the lighter shape.

A future modular-monolith extraction of CatalogService would collapse those 4 projects into
module-folders within one project. The four VSA services are already there.

---

## 4. API: Minimal APIs, not Controllers

**Every HTTP endpoint in NextAurora is a Minimal API endpoint.** No controllers anywhere.

Example from [CatalogEndpoints.cs](../CatalogService/Endpoints/CatalogEndpoints.cs):

```csharp
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapV1ApiGroup("Catalog", "products");

        group.MapGet("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            var product = await bus.InvokeAsync<ProductDto?>(new GetProductByIdQuery(id), ct);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        group.MapPost("/", async (CreateProductCommand command, IMessageBus bus, CancellationToken ct) =>
        {
            var productId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Created($"/api/v1/products/{productId}", new { Id = productId });
        }).RequireAuthorization();
    }
}
```

### Why Minimal APIs

| Reason | Detail |
|---|---|
| **Less ceremony** | One `Map{Verb}(...)` call per endpoint vs a controller class + action attributes + binding ceremony |
| **Better perf** | Minimal APIs skip the MVC pipeline (no controller activation, no action invoker overhead). Microsoft benchmarks show ~10-15% throughput improvement on simple endpoints |
| **Composable** | Endpoint groups + extension methods + the `RouteGroupBuilder` make versioned, tagged, authorized groups one chained call |
| **OpenAPI-first** | `Microsoft.AspNetCore.OpenApi` integrates more directly with the Minimal API metadata model — Scalar + YAML emit work out of the box |
| **Aligned with .NET 10's direction** | New Microsoft templates default to Minimal APIs; controllers are still supported but framed as legacy |
| **Thin shim by design** | Endpoints just dispatch to handlers via Wolverine. Less surface area = fewer places for business logic to leak |

### When you'd use controllers instead

- **Heavy model binding** (form posts, multi-part uploads, custom binders) — controllers' binding pipeline is richer
- **Filters and action conventions** at scale — `[Authorize(Roles = ...)]` + `[ApiVersion(...)]` + custom action filters compose more readably as attributes on a controller than as `.RequireAuthorization(...)` chains
- **Existing team familiarity** — if every developer knows MVC and nobody has touched Minimal APIs, the learning cost may not be worth the wins

We don't have any of those needs, so Minimal APIs are unambiguous.

### The `MapV1ApiGroup` helper

Defined in [NextAurora.ServiceDefaults/Extensions.cs:254](../NextAurora.ServiceDefaults/Extensions.cs#L254):

```csharp
public static RouteGroupBuilder MapV1ApiGroup(this WebApplication app, string tag, string template)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentException.ThrowIfNullOrWhiteSpace(template);

    var trimmed = template.TrimStart('/');
    return app.NewVersionedApi(tag)
        .MapGroup($"/api/v{{version:apiVersion}}/{trimmed}")
        .HasApiVersion(new ApiVersion(1, 0))
        .WithTags(tag);
}
```

**Every endpoint group in every service goes through this** — the policy (default version, tag, route prefix) can't drift across services. CLAUDE.md hard rule:

> **Always use `app.MapV1ApiGroup("Tag", "resource")`** (helper in `NextAurora.ServiceDefaults`) to register a versioned route group. Don't hand-roll `NewVersionedApi(...).MapGroup(...).HasApiVersion(...)` chains.

---

## 5. URL-segment versioning (`/api/v1/...`)

Configured globally in [`AddNextAuroraApiVersioning`](../NextAurora.ServiceDefaults/Extensions.cs#L209), called from `AddServiceDefaults`:

```csharp
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = false;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });
```

Every route lives under `/api/v{version}/...`. The version segment is **required** (`AssumeDefaultVersionWhenUnspecified = false`).

### Why URL-segment over header-based versioning

The "header versioning is more RESTful" argument is academic. URL versioning is what every major public API uses (Stripe, GitHub, Twitter, AWS) because:

| Win | Detail |
|---|---|
| **Visible in logs** | `/api/v1/orders` jumps out of a log line — header versions are invisible without instrumentation |
| **Cacheable** | HTTP caches key on URL — header-based versions need explicit `Vary:` config, which is fragile |
| **Browser-debuggable** | Curl `/api/v1/orders` works from any terminal; testing header-versioned routes from a browser is harder |
| **Plays well with OpenAPI** | Versioned route groups → versioned OpenAPI docs out of the box |

### Why the version segment is required (not assumed)

If `AssumeDefaultVersionWhenUnspecified` were `true`, `/api/products` would silently route to v1. **Why we don't:** the day you ship v2 with a behavior change, every un-versioned call out there is now silently hitting v2. That makes the migration a debugging nightmare. Better: require the version, return 400 if missing, force callers to declare what they expect.

### How v2 will work

Register a side-by-side group with `.HasApiVersion(new ApiVersion(2, 0))`. Existing v1 callers keep hitting the v1 handler unchanged. The compiler keeps both versions building and the URL space cleanly separates them.

---

## 6. OpenAPI + YAML + Scalar UI

Every service emits OpenAPI specs and ships an interactive UI in development.

From [CatalogService/Program.cs](../CatalogService/Program.cs):

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                   // /openapi/v1.json
    app.MapOpenApi("/openapi/{documentName}.yaml");     // /openapi/v1.yaml
    app.MapScalarApiReference();                        // /scalar/v1 — interactive UI
    await app.Services.MigrateDatabaseAsync<CatalogDbContext>();
}
```

| Endpoint | What you get |
|---|---|
| `GET /openapi/v1.json` | Machine-readable spec for tooling (Postman, codegen, gateways) |
| `GET /openapi/v1.yaml` | Same spec, YAML form (Spectral linting, embedding in docs) |
| `GET /scalar/v1` | **Scalar** interactive API reference UI — try-it-out, search, response schemas |

### Why we ship both JSON and YAML

Some tooling prefers YAML (Spectral, kubectl-style configuration, embedding in markdown). The cost is one extra line per service (`Microsoft.AspNetCore.OpenApi` does format selection by route extension). No downside.

### Why Scalar over Swagger UI

Both render OpenAPI as interactive docs. Scalar is the newer entrant and produces a noticeably better UX (cleaner layout, dark mode, real fetch try-it-out without iframe weirdness). Swashbuckle/Swagger UI is the legacy choice and still works; we picked Scalar because it integrates cleanly with `Microsoft.AspNetCore.OpenApi`'s native OpenAPI generation. One line per service, dev-only, gated on `IsDevelopment()`.

### Why dev-only

OpenAPI specs reveal the full API shape — endpoints, schemas, auth requirements. In production we don't want that surface available to attackers reconnoitering. If we wanted production OpenAPI access, we'd put it behind authenticated admin routes.

---

## 7. Authentication — Keycloak + JWT Bearer

Identity provider is **Keycloak**, an Aspire-managed container in local dev. JWT Bearer authentication is wired in [NextAurora.ServiceDefaults/Extensions.cs](../NextAurora.ServiceDefaults/Extensions.cs) (`AddDefaultAuthentication`):

```csharp
// RequireHttpsMetadata is FAIL-CLOSED outside Development: an http authority in Production
// fails loudly at options resolution (framework guard) instead of silently fetching OIDC
// metadata + JWKS over plaintext. Explicit opt-out: Authentication:RequireHttpsMetadata=false
// (logged as a warning) for legitimate internal-http deployments.
var requireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", (bool?)null)
    ?? (authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || !builder.Environment.IsDevelopment());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;  // Keycloak URL
        options.Audience = builder.Configuration["Authentication:Audience"] ?? "nextaurora-api";
        options.RequireHttpsMetadata = requireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,          // explicit — auditable posture
            ClockSkew = TimeSpan.FromSeconds(30),     // default 5 min would double a 5-min token's life
            NameClaimType = "preferred_username",
            RoleClaimType = "realm_access.roles",
        };
    });
```

### Token policy (pinned explicitly in the realm)

`nextaurora-realm.json` pins the Keycloak token policy instead of riding version-dependent
defaults — same explicit-over-implicit posture as `ValidateIssuerSigningKey`/`ClockSkew` above:

| Setting | Value | Why |
|---|---|---|
| `accessTokenLifespan` | 300 (5 min) | Short-lived access tokens; a leaked token dies fast. Pairs with the 30s ClockSkew (the default 5-min skew would double the effective lifetime). |
| `revokeRefreshToken` + `refreshTokenMaxReuse: 0` | rotation, single-use | Every refresh mints a new refresh token and kills the old one — a stolen refresh token dies on the next legitimate renewal. The SPA's `automaticSilentRenew` (oidc-client-ts) handles rotation transparently. |
| `ssoSessionIdleTimeout` | 1800 (30 min) | Refresh window tied to the SSO session — idle sessions can't renew forever. |
| `ssoSessionMaxLifespan` | 36000 (10 h) | Hard ceiling per login regardless of activity. |

Realm changes require a Keycloak re-import locally (fresh container/volume) to take effect.

### Why Keycloak

Following [docs/architecture.md "Authentication"](architecture.md):

| Reason | Detail |
|---|---|
| **Established IdP, battle-tested security** | OWASP / OAuth2 / OIDC compliance handled by experts who do nothing else |
| **Open-source, self-hostable** | No vendor lock-in; runs in your VPC |
| **Supports realms** | Multi-tenant-ready out of the box |
| **Aspire container in dev** | One-line setup; same exact IdP locally and in prod |
| **JWT validation in .NET is trivial** | `AddJwtBearer` is the canonical pattern; nothing custom |

### Alternatives we considered

- **Cognito (AWS)** — only relevant if we were AWS-only. Migration covered in [architecture.md "Deployment"](architecture.md).
- **IdentityServer** — discontinued as OSS (commercial Duende now); pricier and similar capabilities to Keycloak.
- **Hand-rolled JWT minting** — never. Cryptographic security primitives in business code is how breaches happen.

### Token validation rules (from CLAUDE.md)

> All non-public endpoints must use `.RequireAuthorization()`. JWT Bearer authentication.

> Users can only access their own resources. Validate `buyerId` matches authenticated user.

---

## 8. Authorization — `.RequireAuthorization()` + buyer-scope checks

Two layers of authorization:

### 8.1 Endpoint-level: `RequireAuthorization()`

At the group level for entire resource groups, at the endpoint level for individual routes.

From [OrderEndpoints.cs:32](../OrderService/Endpoints/OrderEndpoints.cs#L32):

```csharp
// Group-level: every endpoint below requires a valid JWT.
var group = app.MapV1ApiGroup("Orders", "orders").RequireAuthorization();
```

From [CatalogEndpoints.cs:70](../CatalogService/Endpoints/CatalogEndpoints.cs#L70):

```csharp
// Endpoint-level: GET /products is anonymous, POST requires auth.
group.MapPost("/", async (CreateProductCommand command, IMessageBus bus, CancellationToken ct) =>
{
    var productId = await bus.InvokeAsync<Guid>(command, ct);
    return Results.Created($"/api/v1/products/{productId}", new { Id = productId });
}).RequireAuthorization();
```

### 8.2 Per-endpoint buyer-scope checks

Authentication says "the request has a valid JWT." Authorization says "the request can do *this specific thing*." Buyer-scope is the second part: a logged-in buyer must only see/affect their own orders.

```csharp
group.MapGet("/buyer/{buyerId:guid}", async (Guid buyerId, HttpContext context, IMessageBus bus, CancellationToken ct, int page = 1, int pageSize = 50) =>
{
    var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (userId is null || !Guid.TryParse(userId, out var authenticatedId) || authenticatedId != buyerId)
        return Results.Forbid();    // 403

    // ... reach the handler
});
```

### Why this lives at the endpoint, not in the handler

The JWT principal is an HTTP concept. The Application layer's handlers shouldn't know about `HttpContext`. The endpoint adapts the principal-vs-buyer check before the command crosses the layer boundary — keeps Domain/Application clean.

### What if we needed roles?

We don't today, but the pattern would be `.RequireAuthorization(policy => policy.RequireRole("admin"))` at the endpoint, with role policies registered in `AddServiceDefaults`. The `RoleClaimType = "realm_access.roles"` line in the JWT config means Keycloak's realm roles flow through `User.IsInRole(...)` naturally.

---

## 9. Validation — FluentValidation via Wolverine policy

Three layers of validation, each catching invalid data at a different point:

| Layer | Mechanism | When |
|---|---|---|
| **HTTP / messaging** | FluentValidation via Wolverine's `UseFluentValidation()` policy | Before any handler executes |
| **Domain** | `ArgumentException` / `ArgumentOutOfRangeException` in `Create()` / mutation methods | When entities are constructed or modified |
| **Business rules** | `InvalidOperationException` in domain methods | When invalid state transitions are attempted |

### Why FluentValidation specifically

| Reason | Detail |
|---|---|
| **Composable rules** | `RuleFor`, `Must`, `When`, `ChildRules` — much more readable than chained Boolean expressions |
| **Async-friendly** | `MustAsync` for async predicates (e.g. uniqueness checks); built for async/await |
| **Wolverine integration** | `opts.UseFluentValidation()` runs every command through registered validators *before* the handler |
| **Rich error reporting** | Returns structured `ValidationResult` with property names + messages → GlobalExceptionHandler maps to RFC 7807 |
| **Industry-standard** | Used in most modern .NET stacks; the de facto choice |

### What a validator looks like

From [PlaceOrderCommandValidator.cs](../OrderService/Features/PlaceOrder.cs):

```csharp
public class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.BuyerId).NotEmpty();
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Lines).NotEmpty()
            .WithMessage("Order must contain at least one line item.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}
```

### The Wolverine pipeline order matters

Validation runs **before** `ContextPropagationMiddleware` opens the logger scope. That way 400 responses for invalid commands don't pollute the logger scope or the trace. The handler only ever sees valid messages with a correlation ID already restored.

CLAUDE.md "Communication Patterns" + the Wolverine config in [Program.cs](../OrderService/Program.cs):

```csharp
opts.UseFluentValidation();                  // first — invalid commands rejected
opts.AddNextAuroraContextPropagation();      // then — logger scope opens
opts.Policies.AutoApplyTransactions();       // then — handler runs in a transaction
```

### Why three layers (not just one)

Each layer protects against a different class of error:

1. **Validation layer (FluentValidation)** — bad shape from the client. Required fields, lengths, ranges. Static rules about what makes a valid `PlaceOrderCommand`.
2. **Domain layer (entity factories)** — invariants. `Order.Create()` throws if `buyerId == Guid.Empty` or no lines, even if FluentValidation somehow let it through. Defense in depth.
3. **Business rules (state transitions)** — context. `Order.MarkAsPaid()` throws `InvalidOperationException` if the order isn't currently `Placed`. The same input is valid or invalid depending on current state.

---

## 10. Error handling — `GlobalExceptionHandler` + RFC 7807

All API error responses use **RFC 7807 ProblemDetails** via [GlobalExceptionHandler](../NextAurora.ServiceDefaults/GlobalExceptionHandler.cs), registered once in `AddServiceDefaults` and shared by every service:

```csharp
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const string TraceIdKey = "traceId";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);

        var problemDetails = exception switch
        {
            FluentValidation.ValidationException ve  => CreateValidationProblemDetails(ve, traceId),    // 400 + per-field errors
            DbUpdateConcurrencyException             => new ProblemDetails { Status = 409, Title = "Concurrent modification", ... },
            ArgumentException                        => new ProblemDetails { Status = 400, Title = "Invalid request", ... },
            InvalidOperationException                => new ProblemDetails { Status = 409, Title = "Operation not allowed", ... },
            _                                        => new ProblemDetails { Status = 500, Title = "An unexpected error occurred", Detail = "Please contact support with the trace ID.", ... },
        };

        httpContext.Response.StatusCode = problemDetails.Status ?? 500;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }
}
```

### Why a global handler (vs try/catch in every endpoint)

| Reason | Detail |
|---|---|
| **One place to change** | All HTTP exception handling lives here. Add a new exception type → one switch arm |
| **Endpoints stay thin** | Endpoints just dispatch + return success cases. No defensive scaffolding |
| **Consistent shape** | Every error response is RFC 7807 with `traceId`. Frontend handles one error format |
| **Security** | Never expose internal details (entity IDs, stack traces, SQL fragments) to clients. Log server-side; return generic detail + trace ID |

CLAUDE.md "Security Requirements":

> Never expose internal state, stack traces, or entity IDs in API responses. Log details server-side, return generic errors with correlation IDs to clients.

### The trace ID

Every error response includes `traceId` — the current OTel trace ID (or `httpContext.TraceIdentifier` as fallback). A user reports an error → operator searches logs for the trace ID → full request flow visible. **This is what makes RFC 7807 + OTel a complete observability story.**

### What's in `ValidationProblemDetails`

```json
{
  "status": 400,
  "title": "Validation failed",
  "detail": "One or more validation errors occurred.",
  "traceId": "00-abc123...-def456...-01",
  "errors": {
    "BuyerId": ["BuyerId must not be empty"],
    "Lines": ["Order must contain at least one line item."]
  }
}
```

The `errors` dictionary is keyed by field name and values are arrays of messages. Standard shape; the frontend can iterate and display per-field error states.

---

## 11. Rate limiting

Built-in .NET 7+ `Microsoft.AspNetCore.RateLimiting` middleware, configured per-policy and applied per-endpoint.

From [CatalogService/Program.cs](../CatalogService/Program.cs):

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("search", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromSeconds(10);
        limiter.QueueLimit = 0;
    });
});

app.UseRateLimiter();
```

Applied selectively at the endpoint:

```csharp
group.MapGet("/search", ...).RequireRateLimiting("search");
```

### Why rate-limit search specifically

CLAUDE.md "Security Requirements":

> Rate Limiting: Applied to search and payment endpoints at minimum.

Search hits a relatively expensive `LIKE %query%` query that can't use a B-tree index well. Without rate limiting, a single client scraping the catalog could DOS the database. 30 requests per 10s per client window strikes a balance: real users don't notice; abusive clients hit the wall.

### What we'd add next

- **Payment endpoints** — same rationale, fraud + Stripe rate limits
- **Per-buyer order placement** — prevent rapid-fire reorders
- **Auth endpoints** (when we have them) — slow-loris protection

The pattern is consistent: declare a named policy + apply at the endpoint with `.RequireRateLimiting("policy-name")`.

---

## 12. Communication patterns — REST / gRPC / Service Bus

Three transports, one rule each.

### 12.1 REST (HTTP/JSON) — frontend to service

URL-segment versioned (`/api/v1/...`), JSON request/response, RFC 7807 errors. **Used only for frontend-to-service communication.** Inter-service calls never go over REST.

### 12.2 gRPC — synchronous inter-service queries

For **real-time queries between services** where the caller needs a definitive answer before continuing. Versioned separately via `.proto` `package` declarations.

Example: `PlaceOrderHandler` (OrderService) calls `CatalogGrpcService` (CatalogService) for each line item to validate the product exists and reserve stock — see [PlaceOrder.cs:79](../OrderService/Features/PlaceOrder.cs#L79):

```csharp
var product = await catalogClient.GetProductAsync(lineItem.ProductId, cancellationToken);
if (product is null) throw new InvalidOperationException("...");
var reserved = await catalogClient.ReserveStockAsync(lineItem.ProductId, lineItem.Quantity, cancellationToken);
if (!reserved) throw new InvalidOperationException("...");
```

#### Why gRPC for sync inter-service

| Reason | Detail |
|---|---|
| **Binary protobuf** | ~3-10x smaller payloads than JSON; faster to serialize/deserialize |
| **HTTP/2 multiplexing** | Multiple in-flight requests on one connection — lower connection-establishment overhead |
| **Strong typing across services** | `.proto` files are the contract; generated clients/servers can't disagree on shape |
| **Streaming** | Native support for server/client/bidi streaming (not used today but available) |
| **Built-in deadlines** | Cancellation propagation across the wire |

### 12.3 Azure Service Bus (Wolverine) — async event-driven workflows

For **the order fulfillment saga** — events fan out to multiple subscribers. Configured per service via Wolverine; topology mapped 1:1 in [architecture.md](architecture.md).

Why async events:

- **Temporal decoupling** — OrderService doesn't have to know when PaymentService is ready
- **Multiple subscribers per event** — `OrderPlacedEvent` goes to PaymentService (process payment) AND NotificationService (send "Order Received" email) — two consumers, one publisher
- **Resilience** — if PaymentService is down, the message queues until it recovers. No retries from OrderService
- **Throughput** — async dispatch decouples request latency from downstream work

### When to pick which

| Situation | Pick |
|---|---|
| Frontend → service | REST |
| Service A needs an immediate, definitive answer from Service B | gRPC |
| Service A wants to notify "this thing happened" — fire and forget, possibly to multiple subscribers | Service Bus |

CLAUDE.md "Communication Patterns" codifies this. It's the same decision tree every system has to make; we just made it explicit per service.

---

## 13. Wolverine — covers MediatR + MassTransit, both now commercial

**Wolverine** (Jasper FX) is our command/query dispatcher *and* async messaging framework. To understand why that *and* matters, it helps to clarify what the alternatives actually are — because they're two different libraries solving two different concerns:

| Concern | The traditional .NET pick | What it does |
|---|---|---|
| In-process CQRS dispatch + in-process domain events | **MediatR** | Routes commands/queries to handlers in the same process. `INotification` for in-process pub/sub (the "domain events" pattern in DDD). |
| Cross-service async messaging over a bus | **MassTransit** | RabbitMQ / Azure Service Bus / AWS SQS / etc. Handlers consume events from queues/topics; saga support; transports + transactional outbox. |

Milan Jovanović's book picks **MediatR + MassTransit together** — MediatR for the in-process work, MassTransit for the bus. That's a perfectly fine 2024-era stack. We picked Wolverine because **it covers both concerns in one framework**, and the licensing landscape shifted in 2024–2025:

### The licensing situation (the load-bearing reason)

| Library | Status | Date |
|---|---|---|
| **MediatR** | Commercial-license / SponsorLink ("sponsorware") | 2024 — requires paid sponsorship for commercial use |
| **MassTransit v9** | Going commercial — source-available, paid license required | Announced April 2025; v9 GA Q1 2026; v8 OSS maintenance ends after 2026 |
| **WolverineFx** | **MIT, free for commercial use** | Current |

So Milan Jovanović's book stack (MediatR + MassTransit) — which was free at the time of writing — is now or soon will be **two commercial dependencies**. Picking Wolverine sidesteps both license transitions in one decision.

### What Wolverine gives us

1. **In-process command/query dispatch** (MediatR's job) — `bus.InvokeAsync(command)` routes to a handler class by convention. No `IRequestHandler<T>` interface to implement.
2. **In-process domain events** (MediatR's `INotification` job) — `bus.PublishAsync(@event)` with local-only routing.
3. **Distributed async messaging** (MassTransit's job) — same `bus.PublishAsync(@event)` over Azure Service Bus / AWS SQS / RabbitMQ.
4. **Transactional outbox** — entity write + outgoing message commit in the same DB transaction. MediatR doesn't have this at all; MassTransit has it.
5. **Middleware pipeline** — validation, transactions, logging, retries, our custom context propagation.
6. **FluentValidation integration** — `opts.UseFluentValidation()` runs validators automatically before handlers.
7. **Retry policies** — `opts.OnException<DbUpdateConcurrencyException>().RetryWithCooldown(...)`.

### What Wolverine specifically wins over MassTransit (for the distributed part)

| Wolverine | MassTransit |
|---|---|
| MIT license | Commercial from v9 (Q1 2026); v8 maintained for one more year |
| Convention-based handler discovery — class with `HandleAsync` is a handler | Marker interfaces — `IConsumer<T>` |
| Cascading messages — a handler returns an event; Wolverine publishes it automatically | Manual `await context.Publish(event)` |
| Unified with in-process CQRS dispatch | Only async; need MediatR or hand-rolled mediator for in-process |
| Lighter conceptual surface for simple sagas | Richer state-machine saga support (advantage MassTransit) |

### What Wolverine specifically wins over MediatR (for the in-process part)

| Wolverine | MediatR |
|---|---|
| MIT license | Commercial / SponsorLink (2024+) |
| Same handler shape works for in-process + bus | In-process only — separate framework for bus |
| Transactional outbox built-in | Not applicable (in-process only); pairs with a bus library for distributed work |
| Cascading events from handlers | `INotification`s — works, but not transactionally tied to the parent operation |

### What Wolverine costs

| Cost | Detail |
|---|---|
| **Smaller community** | MediatR has historical dominance — 50× the downloads. Stack Overflow answers, blog posts, recipe code are MediatR-flavored. Wolverine docs are excellent but you're sometimes the first person asking a specific question |
| **Generated handler code** | Wolverine source-generates handler dispatch code at startup ("Dynamic" mode). Faster than reflection but adds a startup phase. Switch to "Static" for AOT-friendly precompiled handlers |
| **Steeper initial learning curve** | More concepts (envelopes, durability, sagas, middleware policies) than MediatR's `IRequest<T>` shape. Once internalized, the productivity gain is real |
| **MassTransit's richer state-machine sagas** | MassTransit's saga DSL is more mature for complex long-running workflows. We don't have that scale of saga complexity; choreography (each service reacts to events independently) is enough |

### What handlers look like

No interface, no marker:

```csharp
public class PlaceOrderHandler(IOrderRepository repo, IEventPublisher pub, ICatalogClient catalog, ILogger<PlaceOrderHandler> log)
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        // ... validate, build, persist, publish event ...
        return order.Id;
    }
}
```

Wolverine finds it because the class name ends with `Handler`, it has a public `HandleAsync` method, and the first parameter is a known message type.

### Wolverine 5→6 upgrade notes

> **Full narrative write-up** (the investigation, the wrong turns, the root cause, the lessons):
> [docs/war-story-wolverine6-outbox-atomicity.md](war-story-wolverine6-outbox-atomicity.md).

We upgraded `WolverineFx.*` 5.39.3 → 6.8.0 (a major version). Build was source-compatible; three runtime breaking changes surfaced, all caught by the integration suite. Encoding them so the next major bump (or a fresh reader) doesn't re-derive:

1. **The runtime code generator was split out of core (GH-2876).** Core `WolverineFx` no longer ships the Roslyn compiler. In the default `TypeLoadMode.Dynamic`, the host throws at startup: *"no `IAssemblyGenerator` (Roslyn) is registered."* Fix: reference `WolverineFx.RuntimeCompilation` (auto-registers) — we added it to `NextAurora.ServiceDefaults` so it flows to every service. Production alternative (deferred): pre-generated static codegen (`dotnet run -- codegen write` + `TypeLoadMode.Static`), which drops the runtime Roslyn dependency for faster cold start / AOT.

2. **`ServiceLocationPolicy` default flipped to `NotAllowed`.** Wolverine's generated handler code resolves dependencies either by inlining them or by falling back to a container lookup ("service location"). When a dependency can't be inlined — e.g. an interface with a factory registration like `IProductCache` over `HybridCache`, or a pooled `DbContext` — codegen falls back to service location, which 6.x now rejects at startup by default. This is a *codegen-strategy* concern, not the service-locator anti-pattern (our handlers use ordinary constructor injection). Fix: `opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed` via the shared `AllowHandlerServiceLocation()` extension in `ServiceDefaults`, called in every service.

3. **In-handler publishing via a constructor-injected `IMessageBus` is no longer transaction-enlisted.** This is the subtle one and it cost the most to find. Wolverine enlists in the handler's outbox transaction only the `IMessageContext` it injects as a **`HandleAsync` method parameter**. The `IEventPublisher` shim wraps a *constructor*-injected `IMessageBus`, which under 6.x is **not** enlisted — a publish through it fires *immediately*, before the handler commits. PaymentService's Acceptor→Gateway split depends on the opposite: the Acceptor persists `Payment(Pending)` and publishes a local `PaymentProcessingRequested` continuation that the Gateway handler reads back. Under 6.x the continuation reached the Gateway *before* the Pending row committed (confirmed by log ordering: the Gateway's `Starting to process` line preceded the `INSERT INTO [Payments]`), so the Gateway found no row, no-op'd, and the payment stuck in `Pending` forever. Three config-level attempts (`UseDurableLocalQueues`, swapping the shim to a constructor `IMessageContext`, both together) did **not** fix it — because none of them gives the handler its *enlisted* context. The fix is one method parameter: `ProcessPaymentHandler.HandleAsync(ProcessPaymentCommand request, IMessageContext messageContext, CancellationToken ct)` and publish the continuation through `messageContext`. See `PaymentService/Features/ProcessPayment.cs`.

   **Cross-service: proven and fixed.** The non-enlistment was *not* PaymentService-specific. We proved it with a rollback test — a `SaveChangesInterceptor` that throws when an `Order` commits, then assert the order rolled back **and** no `OrderPlacedEvent` was dispatched. On the unfixed code the order rolled back but the event was already sent: external publishes were non-atomic too (`OrderService.Tests.Integration` → `PlaceOrder_does_not_dispatch_OrderPlacedEvent_when_the_commit_rolls_back`). Fix applied to every **write-then-publish handler** — `PlaceOrder`, `CreateShipment`, PaymentService Gateway — now publish through the method-injected `IMessageContext`. The now-unused `IEventPublisher` shim was deleted from OrderService and ShippingService (dead port). It's retained only in PaymentService for two paths that are *not* write-then-publish: the Acceptor's **republish** of a terminal event for an already-committed payment (no entity write → inline send is correct), and the `PaymentRecoveryJob`.

   **`PaymentRecoveryJob` (non-handler) — also fixed.** The background sweeper marks timed-out payments `Failed` and publishes `PaymentFailedEvent` outside the handler pipeline, so it has no method-injected context. The fix is Wolverine's **non-handler outbox API**: enroll the `DbContext` in an `IDbContextOutbox`, publish through it, then `SaveChangesAndFlushMessagesAsync()` — which stages the envelope, saves the entity, and commits both in one transaction (replacing the old manual `BeginTransaction → Publish → SaveChanges → Commit`, which silently stopped being atomic on 6.x for the same constructor-`IMessageBus` reason). `IDbContextOutbox` resolves from the existing `AddDbContext` + `UseEntityFrameworkCoreTransactions()` registration — no registration change needed. Proven by the "outbox-in-non-handler" rollback test `PaymentRecoveryAtomicityTests` (CLAUDE.md required it; it now exists). `IEventPublisher` is retained in PaymentService only for the Acceptor's terminal-event republish (an already-committed payment, no entity write → inline send is correct).

---

## 14. Observability — OpenTelemetry + context propagation

Three identifiers flow through every request — HTTP and async — automatically:

| Concept | Source | Header | Logger scope key |
|---|---|---|---|
| Correlation | `X-Correlation-Id` header, or trace ID, or new GUID | `X-Correlation-Id` | `CorrelationId` |
| User | JWT `sub` claim | `X-User-Id` (outgoing) | `UserId` |
| Session | `X-Session-Id` request header | `X-Session-Id` | `SessionId` |

Three middlewares:

- **`CorrelationIdMiddleware`** ([file](../NextAurora.ServiceDefaults/Middleware/CorrelationIdMiddleware.cs)) — HTTP entry point. Sets the IDs into `Activity` baggage + opens a `logger.BeginScope`. Echoes the correlation ID back in the response.
- **`ContextPropagationMiddleware`** ([file](../NextAurora.ServiceDefaults/Messaging/ContextPropagationMiddleware.cs)) — Wolverine incoming-message middleware (async entry point). Reads the same headers from envelopes, restores into `Activity` baggage + logger scope.
- **`OutgoingContextMiddleware`** — Wolverine outgoing middleware. Reads `Activity` baggage and stamps headers onto outgoing envelopes so the next service picks them up.

All three are wired in each service's `Program.cs` via `opts.AddNextAuroraContextPropagation()`.

### What OpenTelemetry gives us

Wired in [ConfigureOpenTelemetry](../NextAurora.ServiceDefaults/Extensions.cs#L68):

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("NextAurora"))
    .WithTracing(t => t
        .AddSource(builder.Environment.ApplicationName)
        .AddSource("Wolverine")
        .AddSource("NextAurora.Messaging")
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx =>
                !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                && !ctx.Request.Path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase))
        .AddGrpcClientInstrumentation()
        .AddHttpClientInstrumentation());
```

OTLP exporter is conditional on `OTEL_EXPORTER_OTLP_ENDPOINT` being set (Aspire injects this automatically). In dev, traces/metrics/logs flow into the Aspire dashboard. In production, point at any OTLP-compatible backend (App Insights, X-Ray via OTel collector, Tempo, Honeycomb, Datadog).

### Why OpenTelemetry, not vendor-specific instrumentation

| Reason | Detail |
|---|---|
| **Vendor-neutral** | The collection layer doesn't know or care where traces go. Swap backends without changing app code |
| **Standard** | OTLP is the W3C/CNCF standard. Every modern backend speaks it |
| **Aspire-native** | Aspire's dashboard ingests OTLP. Zero config for local dev |
| **Future-proof** | The .NET 8+ Microsoft direction is OTel-first; the old `System.Diagnostics.Activity` story converges here |

### What we deliberately don't use

**Serilog + Seq.** We use `Microsoft.Extensions.Logging` (built-in) with structured templates. The `logging.AddOpenTelemetry(...)` line pipes those through OTel. Serilog is excellent but adds a layer above what ME.Logging now does natively; Seq is great for dev but Aspire's dashboard handles the same role with integrated trace/log/metric correlation. See [§15](#15-logging--microsoftextensionslogging-not-serilog) for the full reasoning.

---

## 15. Logging — `Microsoft.Extensions.Logging`, not Serilog

We use the built-in `Microsoft.Extensions.Logging` (ME.Logging) with **structured message templates**:

```csharp
logger.LogInformation("Order {OrderId} for buyer {BuyerId} placed at {PlacedAt}",
    order.Id, order.BuyerId, order.PlacedAt);
```

### CLAUDE.md hard rule

> **Structured logging**: use message templates (`"User {UserId} logged in"`) with parameter placeholders, never string concatenation or interpolation. This is also required for the correlation/user/session scope to work.

**Two reasons:**

1. **Performance.** `$"User {user.Name} logged in"` allocates the string *even if logging is filtered out*. The template form `"User {UserName} logged in"` skips allocation when the level is filtered.
2. **Observability.** Templates produce structured fields (`UserName=joe`) that are queryable in OTLP backends. Concatenated strings are opaque blobs you can't filter by.

### Why not Serilog

Serilog is excellent. Milan Jovanović's book uses Serilog + Seq. We don't, because:

| Reason | Detail |
|---|---|
| **ME.Logging is the modern .NET default** | .NET 8+ has matured the logging primitives to the point Serilog's wins (structured templates, scopes, async sinks) are now first-class |
| **`logging.AddOpenTelemetry(...)` integration** | OTel ingests ME.Logging output directly. Adding Serilog means another adapter layer |
| **One less dependency** | Serilog + Serilog.Extensions.Logging + Serilog.Sinks.* — three or four packages to manage vs zero |
| **Sink decision becomes the OTel decision** | Serilog's value over ME.Logging is largely its sink ecosystem (Seq, Elasticsearch, RollingFile). With OTel, you point the exporter at any backend — same flexibility, less coupling |

### Where Serilog still wins

Specific dev sinks. Seq's UI for filtering structured logs locally is genuinely nicer than Aspire's. If we wanted that, adding Serilog as a parallel pipeline alongside OTel is straightforward — not done because the marginal benefit isn't justified.

### Tight-loop logging

CLAUDE.md hard rule:

> No logging in tight loops: log summaries (`"Processed {Count} items"`), not per-item lines.

Per-item logging at 1000 RPS floods log ingestion and stalls the request. Aggregate instead.

---

## 16. HybridCache — chosen over hand-rolled L1/L2

CatalogService caches `ProductDto` reads using `Microsoft.Extensions.Caching.Hybrid` 10.5.0 — two-tier (in-process MemoryCache L1 + Redis L2) with built-in stampede protection.

### What it gives us

| Feature | Detail |
|---|---|
| **L1 (in-process MemoryCache)** | Microseconds. Hot products served without leaving the replica |
| **L2 (distributed Redis)** | Milliseconds. Survives process restart, shared across replicas |
| **Stampede protection** | N concurrent misses for the same key invoke the factory **once**. The others wait for the result |
| **Tag-based invalidation** | `RemoveByTagAsync` clears L2 and the calling replica's L1 in one operation |
| **Built-in serializer** | Source-generated `System.Text.Json` by default. AOT-friendly |

### Why HybridCache vs hand-rolled L1/L2

We could hand-roll a two-tier cache: check `IMemoryCache`, then `IDistributedCache`, then a DB factory. Three subtle bugs you'd discover under load:

1. **Cache stampede.** N concurrent misses → N factory invocations → DB DDoS. Hand-rolled per-key locking is hard to get right (async-safe, non-reentrant, per-key).
2. **L1/L2 invalidation skew.** Forgetting to invalidate both tiers leaves the local L1 serving stale data until TTL.
3. **Serialization drift.** Ad-hoc `JsonSerializer.Serialize` calls per call site accrete into a poison-pill scenario where old entries can't be deserialized.

HybridCache solves all three by construction.

### What HybridCache doesn't do (yet)

**Cross-replica L1 invalidation.** `Microsoft.Extensions.Caching.Hybrid` 10.x has no backplane. When replica A invalidates, replica B serves stale L1 for the L1 TTL. We're single-replica today, so it doesn't matter. When we deploy multi-replica, the fix is either dropping `LocalCacheExpiration` to 60s or migrating to FusionCache (which has a Redis pub/sub backplane). Tracked in [STATUS.md](STATUS.md).

Full design discussion: [performance-and-data-correctness.md "Decision: distributed read caching with HybridCache"](performance-and-data-correctness.md#decision-distributed-read-caching-with-hybridcache).

---

## 17. Resilience — `Microsoft.Extensions.Http.Resilience` (Polly v8)

Every outbound HTTP call gets resilience by default. Wired in `AddServiceDefaults`:

```csharp
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddStandardResilienceHandler();
    http.AddServiceDiscovery();
});
```

`AddStandardResilienceHandler` is Microsoft's curated wrapper around **Polly v8** with sensible defaults:

| Pipeline component | What it does |
|---|---|
| **Total timeout** | Caps the whole pipeline duration |
| **Retry** | Exponential backoff on 5xx + transport errors |
| **Circuit breaker** | Opens the circuit on sustained failure rates → short-circuits to fast failure |
| **Per-attempt timeout** | Caps each individual retry |
| **Rate limiter** | Prevents thundering-herd on a recovering service |

### Why the standard handler vs custom Polly pipelines

Could we write our own Polly pipeline per HttpClient? Yes. We don't because:

1. **Standard handler is idiomatic .NET 8+.** The team behind it owns Polly v8; the defaults are good
2. **One line gives you the full pattern** — retries, circuit breaker, timeout, rate limit
3. **No drift across services** — every service has the exact same resilience defaults
4. **Customizable per-client when needed** — `AddStandardResilienceHandler().Configure(...)` lets you tune specific HttpClients while keeping the default

### What it doesn't cover

gRPC clients get their own resilience via `AddGrpcClient` extensions; we use the default retry policy there. Service Bus is handled by Wolverine's own retry/backoff machinery.

---

## 18. Testing — xUnit + AwesomeAssertions + NSubstitute + Testcontainers

Per-service unit test projects (`tests/{Service}.Tests.Unit`) plus integration test projects (`tests/{Service}.Tests.Integration`) for the slices that have them (Catalog + Order today).

### Library picks

| Library | Role | Why |
|---|---|---|
| **xUnit** | Test framework | De facto standard in .NET. Constructor-as-setup, `IAsyncLifetime` for async fixture lifecycle, parallel-by-default |
| **AwesomeAssertions** | Fluent assertions | Drop-in fork of FluentAssertions (pre-paywall). Same `result.Should().Be(...)` API. See [§20](#20-library-decisions-reference-table) for the migration rationale |
| **NSubstitute** | Mocking | Less ceremony than Moq (`Substitute.For<T>()`, no `Setup().Returns()` chain). Easier-to-read assertions (`x.Received().Method(...)`) |
| **Bogus** | Test data builders | Realistic-looking fake data (Names, emails, addresses). Faster than hand-coding builders for every test |
| **Microsoft.AspNetCore.Mvc.Testing** | `WebApplicationFactory<TEntryPoint>` | Boots the real API in-process for integration tests |
| **Testcontainers (PostgreSql, Redis, MsSql)** | Real DB containers in tests | The only way to verify EF migrations, real concurrency tokens, real cache behavior |
| **coverlet.collector** | Coverage measurement | Ships with the test SDK. `--collect "XPlat Code Coverage"` produces Cobertura XML |

### Why not Moq

Moq was the standard until late 2023 when [SponsorLink](https://github.com/devlooped/SponsorLink) was added to Moq 4.20 — a controversial telemetry/sponsor-prompt extension. The community split. We picked NSubstitute because:

1. **Cleaner API.** `repo.GetByIdAsync(id).Returns(product)` vs Moq's `mock.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(product)`.
2. **No SponsorLink controversy.**
3. **Same capabilities** — argument matching, call verification, async support, callback support.

### Why AwesomeAssertions (not FluentAssertions)

FluentAssertions was the standard fluent-assertion library. In late 2024, v8 moved to a paid commercial license — Xceed acquired the project. AwesomeAssertions is a community fork of v7 (the last MIT version) that continues the project under the original license.

Migration cost: change the package + namespace. Done in commit `3fd6aee`. See [§20](#20-library-decisions-reference-table).

### Why Testcontainers

Mocking the database with InMemory or SQLite leaves real-DB bugs uncaught:

- Postgres `xmin` concurrency token doesn't exist in SQLite
- EF migrations against the real provider may produce SQL the in-memory provider doesn't model
- HybridCache against real Redis verifies what fake-Redis-in-memory can't

Testcontainers spins up real Postgres / SQL Server / Redis containers per test class. Slower than in-memory but the only way to actually prove the seams work.

Pattern documented in [tests/CatalogService.Tests.Integration/README.md](../tests/CatalogService.Tests.Integration/README.md) and [tests/OrderService.Tests.Integration/README.md](../tests/OrderService.Tests.Integration/README.md).

### CLAUDE.md testing rule

> Unit tests for domain logic and handlers. Integration tests with Testcontainers for infrastructure — `tests/{Service}.Tests.Integration`, booting the real API via `WebApplicationFactory<Program>`. CatalogService + OrderService slices exist; pattern in each project's README.
>
> Integration tests need Docker. On macOS, Docker Desktop's socket is at `~/.docker/run/docker.sock`, not `/var/run/docker.sock` — Testcontainers fails fast with `DockerUnavailableException` unless `DOCKER_HOST` points there.

---

## 19. Build system & static analysis

### Central Package Management

Every project references packages **without versions**. Versions live in [Directory.Packages.props](../Directory.Packages.props):

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.2" />
<PackageVersion Include="WolverineFx" Version="6.8.0" />
```

```xml
<!-- Any .csproj -->
<PackageReference Include="Microsoft.EntityFrameworkCore" />
<PackageReference Include="WolverineFx" />
```

**Why:** one place to bump versions. No drift across 20 projects each pinning their own. Dependabot opens one PR to bump a version; that PR changes one file.

### `Directory.Build.props` — shared build settings

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<AnalysisLevel>latest</AnalysisLevel>
<AnalysisMode>All</AnalysisMode>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

Result: **every analyzer warning is a build error.** Zero warnings tolerated.

### Analyzers in use

| Analyzer | Catches |
|---|---|
| **Built-in (.NET 8+ CA*)** | Async/await, ConfigureAwait, ArgumentNullException patterns |
| **Meziantou.Analyzer (MA*)** | CancellationToken propagation, sealed types where appropriate, StringComparer specification, sync-in-async, async naming |
| **SonarAnalyzer.CSharp (S*)** | Cognitive complexity, dead code, S3168 async void, S4462 blocking-in-async, S2445 lock-on-this |
| **Roslynator.Analyzers (RCS*)** | Style + perf micro-optimizations |
| **Microsoft.CodeAnalysis.BannedApiAnalyzers (RS0030)** | `Task.WaitAll`, `Task.WaitAny`, `Parallel.For`, `Parallel.ForEach`, `Thread.Sleep` — banned via [BannedSymbols.txt](../BannedSymbols.txt) with custom error messages pointing at the right replacement |

### Why this combination

Each analyzer catches a different class of problem. CLAUDE.md "Performance Rules" lays out which hazards we care about; the analyzers above enforce most of them at build time. The one hazard analyzers can't catch (static mutable collections — a structural rather than syntactic issue) gets a CI grep step in [ci.yml](../.github/workflows/ci.yml). See [performance-and-data-correctness.md "Concurrency hazards: what the build enforces"](performance-and-data-correctness.md#concurrency-hazards-what-the-build-enforces) for the full mapping.

### Naming convention

From CLAUDE.md "Coding Standards":

- File-scoped namespaces (`namespace Foo;` not `namespace Foo { }`)
- Private *instance* fields prefixed with `_` (camelCase)
- Constants and `static readonly` fields use PascalCase (not `_camelCase`) — .NET convention
- Async methods suffixed with `Async`
- Interfaces prefixed with `I`
- Use `var` when type is apparent

Enforced via `.editorconfig` naming rules; `EnforceCodeStyleInBuild=true` makes violations build errors.

---

## 20. Library decisions reference table

The full inventory of significant non-Microsoft libraries, what they do, why we picked them, and what the alternatives would be.

| Package | Version | Role | Why this, not [X] |
|---|---|---|---|
| **WolverineFx** | 6.8.0 | In-process CQRS dispatch + distributed async messaging + transactional outbox | Covers what **MediatR** (in-process CQRS — commercial since 2024) and **MassTransit** (distributed messaging — commercial in v9, GA Q1 2026) together do, in one MIT-licensed framework. The combined library + license story is the load-bearing reason |
| **WolverineFx.RabbitMQ** | 6.8.0 | Wolverine transport for RabbitMQ | Broker in every environment (local/CI/Hetzner); swappable for another Wolverine transport if a cloud-managed target lands |
| **WolverineFx.SqlServer / .Postgresql** | 6.8.0 | Wolverine outbox persistence | Same DB as the service, same transaction as the entity write |
| **Microsoft.EntityFrameworkCore** | 10.0.2 | ORM | Standard .NET ORM. See [ef-core.md](ef-core.md) for the full decision |
| **Npgsql.EntityFrameworkCore.PostgreSQL** | 10.0.0 | Postgres EF provider | Only viable Postgres provider for EF Core |
| **Microsoft.EntityFrameworkCore.SqlServer** | 10.0.2 | SQL Server EF provider | Microsoft's first-party SQL Server provider |
| **Dapper** | 2.1.72 | Sanctioned escape hatch from EF for raw SQL | Plumbing only; no current query uses it. See [ef-core.md §19](ef-core.md#19-dapper-escape-hatch) |
| **Microsoft.Extensions.Caching.Hybrid** | 10.5.0 | L1+L2 caching with stampede protection | vs hand-rolled L1/L2 — see §16 |
| **Microsoft.Extensions.Caching.StackExchangeRedis** | 10.0.2 | Redis L2 backend for HybridCache | Standard ASP.NET Core Redis integration |
| **Microsoft.Extensions.Http.Resilience** | 10.1.0 | Wraps Polly v8 with curated defaults | vs custom Polly pipelines — one line gives the full pattern |
| **FluentValidation.DependencyInjectionExtensions** | (latest) | DI integration for FluentValidation validators | Standard FluentValidation auto-discovery |
| **WolverineFx.FluentValidation** | 6.8.0 | Wolverine pipeline integration | Runs validators before handlers |
| **Asp.Versioning.Http** | 10.0.0 | URL-segment API versioning | vs header versioning — see §5 |
| **Asp.Versioning.Mvc.ApiExplorer** | 10.0.0 | OpenAPI integration for versioned routes | Required for `MapV1ApiGroup` helper |
| **Microsoft.AspNetCore.OpenApi** | 10.0.2 | OpenAPI emission | First-party, replaces Swashbuckle for new projects |
| **Scalar.AspNetCore** | 2.14.11 | Interactive API reference UI | vs Swashbuckle UI — newer, cleaner UX |
| **Grpc.AspNetCore** | (latest) | gRPC server (CatalogService) | Standard .NET gRPC |
| **Grpc.Net.ClientFactory** | (latest) | gRPC client factory (OrderService) | DI-friendly gRPC client registration |
| **OpenTelemetry.Extensions.Hosting** | 1.15.3 | OTel integration with ASP.NET Core hosting | Standard OTel .NET integration |
| **OpenTelemetry.Exporter.OpenTelemetryProtocol** | 1.15.3 | OTLP exporter for traces/metrics/logs | Vendor-neutral; OTLP is the standard |
| **OpenTelemetry.Instrumentation.AspNetCore / .Http / .Runtime / .GrpcNetClient** | 1.15.0 / .x | Auto-instrumentation for HTTP, gRPC, runtime | The auto-instrumentation set we need |
| **xunit + xunit.runner.visualstudio** | 2.9.3 / 3.0.2 | Test framework | Standard .NET test framework |
| **AwesomeAssertions** | 9.4.0 | Fluent assertions (FluentAssertions fork) | vs FluentAssertions 8 — that went paid-license; AwesomeAssertions is the MIT fork |
| **NSubstitute** | 5.3.0 | Mocking | vs Moq — Moq 4.20 added SponsorLink (controversial), NSubstitute has a cleaner API anyway |
| **Bogus** | 35.6.1 | Realistic test data generation | Standard for .NET; faster than hand-coding test builders |
| **Microsoft.AspNetCore.Mvc.Testing** | 10.0.8 | `WebApplicationFactory<TEntryPoint>` for integration tests | Microsoft's first-party integration-test harness |
| **Testcontainers.PostgreSql / .Redis / .MsSql** | 4.11.0 | Real DB containers in tests | Only reliable way to test real provider behavior |
| **coverlet.collector** | 10.0.0 | Coverage XML for `dotnet test --collect "XPlat Code Coverage"` | Ships with the test SDK |
| **BenchmarkDotNet** | 0.15.8 | Micro-benchmark harness | Standard .NET benchmarking |
| **Meziantou.Analyzer** | 2.0.257 | Static analysis | CancellationToken, async patterns, StringComparer |
| **SonarAnalyzer.CSharp** | 10.16.0.128591 | Static analysis | Cognitive complexity, dead code, security patterns |
| **Roslynator.Analyzers** | 4.14.1 | Static analysis | Style + perf micro-optimizations |
| **Microsoft.CodeAnalysis.BannedApiAnalyzers** | 3.3.4 | Build-time bans for `Task.WaitAll` etc. | Combined with `BannedSymbols.txt` |
| **Aspire.Hosting.* / Aspire.AppHost.Sdk** | 13.3.0 | Local-dev orchestration (Aspire) | The .NET orchestrator for local microservices dev |
| **Asp.Versioning.* / JasperFx.Resources** | 10.0.0 / (latest) | Versioning + Wolverine resource setup | Required infrastructure |

---

## 21. Crib sheet

A condensed walkthrough of the key decisions, each mapped to a section above. Useful as a refresher.

### "Walk me through the architecture."

> NextAurora is a .NET 10 microservices platform with 5 backend services — Catalog, Order, Payment, Shipping, Notification. Each is independently deployable with its own database. Catalog and Shipping run on Postgres; Order and Payment on SQL Server; Notification is stateless. Cross-service communication is gRPC for synchronous queries (Order calls Catalog to validate products) and RabbitMQ for asynchronous workflow events. **The per-service shape varies by complexity**: CatalogService — the largest — uses Clean Architecture (Domain/Application/Infrastructure/Api as four csprojs). The other four are smaller (≤2 aggregates each) and use Vertical Slice Architecture: a single project with feature folders, Domain/, Infrastructure/, Endpoints/. The cross-service diff is intentional and documented in CLAUDE.md.

### "Why microservices instead of modular monolith?"

> The honest answer is that for a real greenfield production system, modular monolith would be the right starting point — Milan Jovanović's book on the topic argues that explicitly. We picked microservices because the *purpose* of this project is demonstrating distributed-system patterns in their natural habitat: the saga choreography across services, the transactional outbox solving a real dual-write problem, real network hops over gRPC, real concurrent updates defended by concurrency tokens. For a portfolio project where the patterns themselves are the deliverable, microservices is the point. For real production, I'd start as a modular monolith and extract.

### "Why Minimal APIs over Controllers?"

> Three reasons: less ceremony per endpoint, ~10-15% better throughput on simple endpoints because there's no MVC pipeline overhead, and cleaner composition via endpoint groups. We have a `MapV1ApiGroup` helper in ServiceDefaults that gives every service the same versioned route shape `/api/v1/...` in one chained call. Controllers still win for heavy model binding or attribute-heavy auth policies, but we don't need either of those.

### "Why Wolverine?"

> Worth clarifying the alternatives first because they're often confused. MediatR is in-process CQRS dispatch — commands, queries, and in-process domain events via `INotification`. MassTransit is distributed messaging over a bus — RabbitMQ, Azure Service Bus, AWS SQS. They solve different problems and many .NET shops use them together. Wolverine covers both in one framework with one handler shape.
>
> Why that matters specifically in 2025-2026: MediatR went commercial in 2024 (sponsorware), and MassTransit v9 is going commercial in Q1 2026 with v8's open-source maintenance ending after 2026. So the traditional "MediatR + MassTransit" stack is now or soon will be two paid commercial dependencies. Wolverine is MIT — picking it sidesteps both license transitions in one decision.
>
> Beyond licensing: Wolverine has a built-in transactional outbox (MediatR has none; MassTransit has one), convention-based handler discovery so there's no `IRequestHandler<T>` / `IConsumer<T>` marker interface to implement, and cascading messages — a handler can return an event and Wolverine publishes it inside the same transaction. The cost is a smaller community than MediatR's; you're sometimes the first person asking a specific Stack Overflow question. Worth it for the license and the unified model.

### "How do you handle the dual-write problem?"

> Wolverine's transactional outbox. The entity write and the outgoing message persist to a `wolverine` schema in the same DB, in the same EF Core transaction. After the handler returns, both commit together — a background dispatcher then sends the staged message to Service Bus with retry. So "order saved but event lost" can't happen.

### "Why URL versioning over header versioning?"

> Pragmatic wins: URL versions are visible in logs and dashboards, cacheable, debuggable from a browser, and play well with OpenAPI tooling. The "header versioning is more RESTful" argument is academic — every major public API (Stripe, GitHub, AWS) uses URL versioning. We also require the version segment to be present, not assumed — so the day we ship v2, no caller is silently still hitting v1.

### "Why three layers of validation?"

> Each catches a different class of error. FluentValidation at the Wolverine pipeline boundary catches bad input shape — required fields, lengths, ranges. The domain entity's `Create()` factory catches invariant violations even if validation somehow let them through — defense in depth. The state-transition methods catch context-dependent rules — `Order.MarkAsPaid()` requires the order to currently be `Placed`. Each layer protects against assumptions the other can't make.

### "Why Keycloak?"

> Established IdP with proven security; open-source so we control the deployment; supports OAuth2/OIDC out of the box; multi-realm-ready when we need multi-tenancy. Comes up as one container in Aspire for local dev. Alternatives like Cognito are AWS-only; IdentityServer went commercial. Keycloak is the modern open default.

### "Why HybridCache and not just `IMemoryCache` or just Redis?"

> HybridCache gives us a two-tier cache with stampede protection built in. The classical hand-rolled L1+L2 has three subtle bugs you discover under load: concurrent misses for the same key invoke the factory N times instead of once; forgetting to invalidate one tier serves stale data; ad-hoc JSON serialization choices drift over time. HybridCache solves all three by construction. One caveat: cross-replica L1 invalidation doesn't exist in 10.x — we're single-replica today, so it doesn't matter; when we deploy multi-replica we'll either shorten L1 TTL or migrate to FusionCache for its backplane.

### "Why Microsoft.Extensions.Logging instead of Serilog?"

> The modern .NET 8+ default has matured to the point Serilog's wins — structured templates, scopes, async sinks — are now first-class in `Microsoft.Extensions.Logging`. We pipe ME.Logging through `logging.AddOpenTelemetry(...)` so the structured fields reach whatever OTel backend we point at. Serilog still wins for specific sinks like Seq with its filtering UI; we don't use it because the marginal benefit doesn't justify the extra dependency layer, and Aspire's dashboard already gives us trace/log/metric correlation in dev.

### "Why AwesomeAssertions, not FluentAssertions?"

> FluentAssertions went paid-license in v7+ — Xceed acquired the project. AwesomeAssertions is a community fork of the last MIT version that continues development under the original license. Migration was a one-commit job: change the package + namespace. Same `Should().Be(...)` API.

### "What's your testing strategy?"

> xUnit + AwesomeAssertions + NSubstitute for unit tests on domain logic and handlers — those mock the repository and bus. Integration tests use `Microsoft.AspNetCore.Mvc.Testing`'s `WebApplicationFactory<Program>` to boot the real API in-process against Testcontainers — real Postgres, real Redis, real SQL Server. That's the only way to verify EF migrations, real concurrency tokens, and real cache behavior. Two integration slices today: Catalog and Order. The cross-service saga over the real ASB wire is the next slice — deferred because it requires the ASB emulator container which is heavier.

### "How does observability work?"

> OpenTelemetry, with three custom middlewares for context propagation. `CorrelationIdMiddleware` runs on HTTP requests, sets `CorrelationId/UserId/SessionId` into `Activity` baggage and opens a `logger.BeginScope`. `ContextPropagationMiddleware` is the Wolverine async equivalent — restores the same IDs from envelope headers when consuming a message. `OutgoingContextMiddleware` stamps them onto outgoing envelopes. The OTLP exporter sends traces, metrics, and logs to whatever backend you configure — Aspire dashboard locally, anything OTLP-compatible in prod (App Insights, X-Ray via collector, Tempo, Honeycomb).

### "How do you enforce coding standards?"

> `TreatWarningsAsErrors=true` + `AnalysisMode=All` makes every analyzer warning a build error. Five analyzer packages: built-in CA*, Meziantou (cancellation token propagation), SonarAnalyzer (cognitive complexity, sync-in-async), Roslynator (style/perf micro-optimizations), and BannedApiAnalyzers with a repo-root `BannedSymbols.txt` that compile-rejects `Task.WaitAll`, `Parallel.For`, `Thread.Sleep`, etc. with custom error messages pointing at the right replacement. One concurrency hazard the analyzers can't catch — shared static mutable collections — is enforced by a CI grep step.

### "Why not Dapr?"

> Dapr's pitch — one runtime that covers service invocation, pub/sub, state, secrets, and distributed locks — is real for the right shop, but it's not us. Three of the five building blocks already have *better-integrated* equivalents here: Wolverine's pub/sub has a transactional outbox committed in the same `SaveChanges` as the entity write (Dapr added a state-store outbox in v1.12, but it's coupled to its state-management model — see §22 for the distinction), gRPC gives us typed contracts (Dapr invocation is stringly-typed), and HybridCache has stampede protection. Secrets are covered by the standard `IConfiguration` chain (env locally, Key Vault in prod). The only building block we don't have is distributed locks — and we don't need them today: every aggregate has optimistic concurrency, every event handler is idempotent, no service runs a singleton background job. When we eventually do need a lock (likely for a scheduled sweep job on a multi-replica service), the natural answer is the `DistributedLock` library against our existing Postgres/SQL Server/Redis — no new runtime, no sidecar. Dapr makes sense for polyglot teams or hard multi-cloud portability requirements; we're neither. Section 22 has the full analysis.

### "How would you scale this?"

> Three layers. **Vertically:** each service can scale to a larger VM. **Horizontally:** add replicas — but Catalog needs FusionCache before we deploy multi-replica because HybridCache 10.x lacks a backplane. **Database:** move from Aspire-managed local containers to RDS / managed Postgres / Azure SQL. The whole deployment story (AWS via SNS+SQS replacing RabbitMQ) is laid out in [architecture.md "Deployment"](architecture.md). Wolverine's transport-agnostic design means swapping `WolverineFx.AzureServiceBus` for `WolverineFx.AmazonSqs` is a Program.cs change — handlers, contracts, the outbox all stay the same.

---

## 22. Dapr — considered, not adopted (and distributed locks)

A recurring question for any .NET microservices project: **should we use Dapr?** The pitch — one runtime that abstracts messaging, state, secrets, service invocation, and distributed locks behind a unified client+sidecar model — is genuinely appealing for the right shop. **For NextAurora it isn't a fit**, and this section walks the analysis so the next time someone reads a Dapr post we don't relitigate from scratch.

### What Dapr is

Sidecar-pattern building blocks. Every service deploys with a `daprd` process alongside it (localhost ↔ sidecar over HTTP/gRPC). The app calls into the sidecar; the sidecar talks to the chosen backend (broker, store, secrets vault, lock provider) via a YAML-configured *component*. The marketing pitch: one client API across all backends, swap backends by editing one YAML file.

### The building blocks Dapr claims to solve, mapped to NextAurora

The classic five, plus **Workflow** — the durable-orchestration block that newer Dapr write-ups (e.g. Milan Jovanović's "Building Dapr Workflows in .NET With Aspire") lead with. It's a distinct decision from the other five and gets its own subsection below.

| Dapr building block | What NextAurora uses today | Verdict |
|---|---|---|
| **Service invocation** | gRPC client factory + `.proto`-defined contracts (Order → Catalog product validation) | Covered with typed contracts |
| **Pub/sub** | Wolverine + RabbitMQ + transactional outbox (§13) | Covered — and *better-integrated* (outbox in the EF `SaveChanges`) |
| **State store** | EF Core (aggregates with concurrency tokens) + HybridCache (L1+L2 with stampede protection, §16) | Covered |
| **Secrets** | Standard `IConfiguration` provider chain — env vars locally, Azure Key Vault in prod | Covered |
| **Distributed locks** | None today | Not needed today — see below |
| **Workflow** (durable orchestration) | Choreography saga — Wolverine handlers reacting to events, no central orchestrator (architecture.md) | Different *topology*, not a missing capability — see "Dapr Workflow" below |

### Why Dapr would *regress* what we have

1. **The transactional outbox is better-integrated in Wolverine.** Dapr *does* have a transactional outbox (added v1.12) — earlier versions of this doc claimed it didn't; that's now wrong and the correction matters. But Dapr's outbox is coupled to its **state-store** model: the atomic unit is "Dapr state write + Dapr pub/sub publish," routed through a configured state component. Ours is coupled to **EF Core**: the entity write and the staged message commit in the *same* `SaveChangesAsync` against the service's own DbContext (`PersistMessagesWithSqlServer` + `AutoApplyTransactions`), with the message store living in the same database as the aggregates. For a stack where the source of truth is EF aggregates (not a Dapr state component), Wolverine's outbox is the natural fit and Dapr's would mean routing writes through Dapr's state abstraction to get the atomicity — adopting Dapr's data model, not just its messaging. So: not "Dapr can't," but "Dapr's version assumes a Dapr-shaped persistence layer we don't have."
2. **The "swap brokers via YAML" claim oversells portability.** Broker semantics differ — Service Bus topic+subscription with sessions, FIFO, dead-lettering, and the globally-unique-subscription-name constraint doesn't map to a Kafka swap by editing one YAML line. The portability is real only for trivial fire-and-forget publishes. The real cross-cloud swap (ASB → SQS) is already in scope and handled by switching `WolverineFx.AzureServiceBus` to `WolverineFx.AmazonSqs` in `Program.cs` — same handler shape, same outbox guarantees.
3. **Typed contracts become stringly-typed Dapr invocations.** gRPC's compile-time safety and `.proto`-based versioning would disappear behind generic `InvokeMethodAsync<T>(appId, method, payload)` calls.
4. **Sidecar adds a hop on every call.** localhost → sidecar → network → sidecar → service. For gRPC product validation on the order hot path, measurable cost we don't pay today.
5. **Speculative coupling at a runtime level.** The CLAUDE.md "interfaces earn their keep through consumer substitution" rule applies to runtimes too. Dapr adds an abstraction layer to enable swaps we've never needed and aren't planning. The five SDKs Dapr "replaces" in the marketing pitch are largely a strawman for our stack: Wolverine is *one* SDK covering messaging + outbox + middleware; secrets are stock .NET config; caching is HybridCache; service-to-service is gRPC. That's a coherent .NET-native stack, not five disconnected concerns.

### When Dapr WOULD make sense (not us)

- **Polyglot microservices** — half your services are Go or Python and you need a single ops story for messaging/secrets/state.
- **Hard multi-cloud portability** — broker/secrets-store portability is a hard requirement, not a "nice to have".
- **Greenfield without an opinionated stack** — Dapr's batteries-included story is real if you don't already have Wolverine + Aspire + EF + Microsoft.Identity wired up.
- **Teams without deep .NET expertise** wanting runtime-level abstractions.

NextAurora is none of these.

### Dapr Workflow — the durable-orchestration building block (separate decision)

Newer Dapr content leads with **Workflow**, not the classic five — durable execution where you write a multi-step process as ordinary C# that reads top to bottom, and the engine (built on the Durable Task Framework, same lineage as Azure Durable Functions) records each step to a state store and replays on crash. It's a genuinely good piece of technology; this is not a dismissal.

**But adopting it is an orchestration-vs-choreography decision, not a "do we have durable multi-step processes" gap — and we already made the topology call the other way.** NextAurora's order saga is **choreography**: each service reacts to events independently (`OrderPlaced` → Payment, `PaymentSucceeded` → Shipping, …), there is no central orchestrator, and durability comes from the transactional outbox + durable Service Bus subscriptions + idempotent handlers (CLAUDE.md "Durability ≠ replay"). Dapr Workflow is the **orchestration** alternative: one workflow body drives the steps. Both solve "survive a restart without double-charging"; they differ on *where the flow lives*.

Why choreography is right *today*: the saga is shallow (4 steps), event-shaped, and sub-second per step — the article's opening complaint ("business logic scattered across handlers, nobody can read the flow top to bottom") is the real cost of choreography, but at this depth it's cheap, and the trade buys loose coupling + no orchestrator to operate.

**What would flip it** — and this is the honest trigger, not "never": if the failed-payment / failed-ship **compensation** work (issue #101) grows into genuinely complex stateful flow — timeouts, retries-with-backoff as first-class steps, human-approval gates, multi-day waits, or "where is order X stuck?" becoming unanswerable from event logs — then a durable orchestrator earns its keep, because *that* is exactly the readability/observability problem choreography is worst at. At that point the evaluation is a three-way bake-off, **not an automatic Dapr adoption**: **Temporal** (best-in-class durable execution, hours-to-days workflows) vs **Azure Durable Functions** (already named in [performance-and-data-correctness.md](performance-and-data-correctness.md) as the cloud-managed option) vs **Dapr Workflow** — and Dapr Workflow only wins that bake-off if we'd *also* adopted Dapr for the other building blocks (which the analysis above says we wouldn't). A workflow engine without the rest of Dapr is a Temporal/Durable-Functions decision wearing a Dapr label.

So: **considered, and the topology was chosen deliberately** — choreography now, with a concrete (not hand-wavy) trigger to re-evaluate durable orchestration if #101's compensation logic gets gnarly. The greenfield answer is constraint-dependent: a polyglot or already-on-Dapr shop might reach for Dapr Workflow on day one; for our .NET-native single-team stack it's a "when the saga complexity demands it" call, evaluated against Temporal and Durable Functions on the merits.

### Distributed locks — what they are, when you need them

The one Dapr building block we don't have a direct equivalent for. Walking through it honestly:

#### What distributed locks are for

Mutual exclusion across process boundaries when the database alone can't guard the critical section:

1. **Leader election for singleton workers** — a cron-style job that must run on exactly one instance at a time (e.g. "every 5 min, expire abandoned carts").
2. **Cache stampede prevention** — when a hot cache key expires and 50 requests hit, only one rebuilds it. (HybridCache already solves this for our case — see §16.)
3. **Cross-aggregate critical sections** — operations spanning multiple aggregates where you can't wrap them in a single transaction (rare, usually a design smell).
4. **External-resource coordination** — calling a non-idempotent third-party API where you must serialize calls per-key.

#### What people reach for distributed locks for, but shouldn't

The database almost always solves the problem first:

| Goal | Use this, not a distributed lock |
|---|---|
| Concurrent updates to the same row | Optimistic concurrency (`xmin` / `RowVersion`) — every NextAurora aggregate has this |
| Prevent duplicate creation | Unique constraint |
| "Reserve a seat / room / SKU for 10 min" | Reservation row with TTL + status column, unique constraint on the scarce dimension |
| Idempotent event processing | Idempotency-key column + unique constraint, *or* Wolverine envelope-id deduplication |
| Serialize a critical section in one DB | `SELECT ... FOR UPDATE` / Postgres advisory lock |
| Atomic entity-write + event-publish | Transactional outbox — Wolverine gives us this |

The "3 instances race for room 101" hotel-booking example in Dapr marketing is the textbook case people cite, but in production you'd model it as a `Reservation` row with a unique constraint on `(roomId, dateRange)` — the database rejects the loser. No distributed lock needed, no extra runtime.

#### Walking each NextAurora service

| Service | Concurrent-access concern | How it's handled today |
|---|---|---|
| CatalogService | Stock updates from concurrent orders | Optimistic concurrency (Postgres `xmin`) |
| OrderService | Concurrent updates to the same Order | Optimistic concurrency (SQL Server `RowVersion`) |
| PaymentService | Duplicate payment processing | Idempotency key on the command + idempotent handler |
| ShippingService | Duplicate event processing | Idempotent handlers (Wolverine envelope ID dedup) |
| NotificationService | Stateless — no shared mutable state | N/A |

None of them need distributed locks today.

#### When we might need them (future)

The most plausible trigger: a **scheduled background job that must run on exactly one instance** when we scale a service past a single replica. Plausible candidates:

- "Every 5 minutes, expire abandoned orders" (background sweep in OrderService)
- "Nightly, rebuild bestseller cache" (CatalogService)
- "Every minute, retry failed notifications" (NotificationService — if we ever add a retry queue)

When that need appears, the .NET-native answer is **[DistributedLock](https://github.com/madelson/DistributedLock)** — a library that gives `IDistributedLock` over a backend we already run:

- `DistributedLock.Postgres` — Postgres advisory locks via the existing connection (zero new infra; CatalogService, ShippingService)
- `DistributedLock.SqlServer` — `sp_getapplock` via the existing connection (OrderService, PaymentService)
- `DistributedLock.Redis` — Redlock pattern over the existing Redis instance (any service)

The migration path is: add the library, pick the backend matching the service's existing DB, wrap the job in `await using (await _lock.AcquireAsync(...))`. No new runtime, no new ops surface, no sidecar. **We'd only reach for Dapr if we also needed three or four of its other building blocks simultaneously — and we don't.**

For cross-replica cache invalidation specifically (the other place "distributed coordination" could in principle appear), the answer is FusionCache's backplane, not a lock — see [performance-and-data-correctness.md §"Distributed lock for invalidation"](performance-and-data-correctness.md) and §16 of this doc.

### Verdict

Dapr stays in the **"considered, not adopted"** column. Revisit if any of the following becomes true:
- We add non-.NET services to the stack (polyglot trigger).
- A hard multi-cloud portability requirement appears that the Wolverine transport swap doesn't already cover.
- We find ourselves wanting three or more Dapr building blocks simultaneously, not just one.

For distributed locks specifically: not needed today, `DistributedLock` library is the natural future fit when (not if) a singleton-job requirement appears.

---

## See also

- [CLAUDE.md](../CLAUDE.md) — the canonical hard-rule list (Performance Rules, Coding Standards, Communication Patterns, Security Requirements)
- [docs/ef-core.md](ef-core.md) — companion deep-dive on EF Core usage
- [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) — why each performance/correctness rule exists, with full rationale
- [docs/architecture.md](architecture.md) — service diagrams, communication matrix, AWS deployment plan
- [docs/how-it-works.md](how-it-works.md) — developer walkthrough; reads bottom-up from Program.cs through the saga
- [docs/STATUS.md](STATUS.md) — cross-session entry point: recently landed, next, open issues
