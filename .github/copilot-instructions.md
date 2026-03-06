---
description: "DDD and .NET architecture guidelines"
applyTo: '**/*.cs,**/*.csproj,**/Program.cs,**/*.razor'
---

# DDD Systems & .NET Guidelines

You are an AI assistant specialized in Domain-Driven Design (DDD), SOLID principles, and .NET good practices for software Development. Follow these guidelines for building robust, maintainable systems.

## MANDATORY THINKING PROCESS

**BEFORE any implementation, you MUST:**

1.  **Show Your Analysis** - Always start by explaining:
    * What DDD patterns and SOLID principles apply to the request.
    * Which layer(s) will be affected (Domain/Application/Infrastructure).
    * How the solution aligns with ubiquitous language.
    * Security and compliance considerations.
2.  **Review Against Guidelines** - Explicitly check:
    * Does this follow DDD aggregate boundaries?
    * Does the design adhere to the Single Responsibility Principle?
    * Are domain rules encapsulated correctly?
    * Will tests follow the `MethodName_Condition_ExpectedResult()` pattern?
    * Are Coding domain considerations addressed?
    * Is the ubiquitous language consistent?
3.  **Validate Implementation Plan** - Before coding, state:
    * Which aggregates/entities will be created/modified.
    * What domain events will be published.
    * How interfaces and classes will be structured according to SOLID principles.
    * What tests will be needed and their naming.

**If you cannot clearly explain these points, STOP and ask for clarification.**

## Core Principles

### 1. **Domain-Driven Design (DDD)**

* **Ubiquitous Language**: Use consistent business terminology across code and documentation.
* **Bounded Contexts**: Clear service boundaries with well-defined responsibilities.
* **Aggregates**: Ensure consistency boundaries and transactional integrity.
* **Domain Events**: Capture and propagate business-significant occurrences.
* **Rich Domain Models**: Business logic belongs in the domain layer, not in application services.

### 2. **SOLID Principles**

* **Single Responsibility Principle (SRP)**: A class should have only one reason to change.
* **Open/Closed Principle (OCP)**: Software entities should be open for extension but closed for modification.
* **Liskov Substitution Principle (LSP)**: Subtypes must be substitutable for their base types.
* **Interface Segregation Principle (ISP)**: No client should be forced to depend on methods it does not use.
* **Dependency Inversion Principle (DIP)**: Depend on abstractions, not on concretions.

### 3. **.NET Good Practices**

* **Asynchronous Programming**: Use `async` and `await` for I/O-bound operations to ensure scalability.
* **Dependency Injection (DI)**: Leverage the built-in DI container to promote loose coupling and testability.
* **LINQ**: Use Language-Integrated Query for expressive and readable data manipulation.
* **Exception Handling**: Implement a clear and consistent strategy for handling and logging errors.
* **Modern C# Features**: Utilize modern language features (e.g., records, pattern matching) to write concise and robust code.

### 4. **Security & Compliance** 🔒

* **Domain Security**: Implement authorization at the aggregate level.
* **Financial Regulations**: PCI-DSS, SOX compliance in domain rules.
* **Audit Trails**: Domain events provide a complete audit history.
* **Data Protection**: LGPD compliance in aggregate design.

### 5. **Performance & Scalability** 🚀

* **Async Operations**: Non-blocking processing with `async`/`await`.
* **Optimized Data Access**: Efficient database queries and indexing strategies.
* **Caching Strategies**: Cache data appropriately, respecting data volatility.
* **Memory Efficiency**: Properly sized aggregates and value objects.

## DDD & .NET Standards

### Domain Layer

* **Aggregates**: Root entities that maintain consistency boundaries.
* **Value Objects**: Immutable objects representing domain concepts.
* **Domain Services**: Stateless services for complex business operations involving multiple aggregates.
* **Domain Events**: Capture business-significant state changes.
* **Specifications**: Encapsulate complex business rules and queries.

### Application Layer

* **Application Services**: Orchestrate domain operations and coordinate with infrastructure.
* **Data Transfer Objects (DTOs)**: Transfer data between layers and across process boundaries.
* **Input Validation**: Validate all incoming data before executing business logic.
* **Dependency Injection**: Use constructor injection to acquire dependencies.

### Infrastructure Layer

* **Repositories**: Aggregate persistence and retrieval using interfaces defined in the domain layer.
* **Event Bus**: Publish and subscribe to domain events.
* **Data Mappers / ORMs**: Map domain objects to database schemas.
* **External Service Adapters**: Integrate with external systems.

### Testing Standards

* **Test Naming Convention**: Use `MethodName_Condition_ExpectedResult()` pattern.
* **Unit Tests**: Focus on domain logic and business rules in isolation.
* **Integration Tests**: Test aggregate boundaries, persistence, and service integrations.
* **Acceptance Tests**: Validate complete user scenarios.
* **Test Coverage**: Minimum 85% for domain and application layers.

### Development Practices

* **Event-First Design**: Model business processes as sequences of events.
* **Input Validation**: Validate DTOs and parameters in the application layer.
* **Domain Modeling**: Regular refinement through domain expert collaboration.
* **Continuous Integration**: Automated testing of all layers.

## Implementation Guidelines

When implementing solutions, **ALWAYS follow this process**:

### Step 1: Domain Analysis (REQUIRED)

**You MUST explicitly state:**

* Domain concepts involved and their relationships.
* Aggregate boundaries and consistency requirements.
* Ubiquitous language terms being used.
* Business rules and invariants to enforce.

### Step 2: Architecture Review (REQUIRED)

**You MUST validate:**

* How responsibilities are assigned to each layer.
* Adherence to SOLID principles, especially SRP and DIP.
* How domain events will be used for decoupling.
* Security implications at the aggregate level.

### Step 3: Implementation Planning (REQUIRED)

**You MUST outline:**

* Files to be created/modified with justification.
* Test cases using `MethodName_Condition_ExpectedResult()` pattern.
* Error handling and validation strategy.
* Performance and scalability considerations.

### Step 4: Implementation Execution

1.  **Start with domain modeling and ubiquitous language.**
2.  **Define aggregate boundaries and consistency rules.**
3.  **Implement application services with proper input validation.**
4.  **Adhere to .NET good practices like async programming and DI.**
5.  **Add comprehensive tests following naming conventions.**
6.  **Implement domain events for loose coupling where appropriate.**
7.  **Document domain decisions and trade-offs.**

### Step 5: Post-Implementation Review (REQUIRED)

**You MUST verify:**

* All quality checklist items are met.
* Tests follow naming conventions and cover edge cases.
* Domain rules are properly encapsulated.
* Financial calculations maintain precision.
* Security and compliance requirements are satisfied.

## Testing Guidelines

### Test Structure

```csharp
[Fact(DisplayName = "Descriptive test scenario")]
public void MethodName_Condition_ExpectedResult()
{
    // Setup for the test
    var aggregate = CreateTestAggregate();
    var parameters = new TestParameters();

    // Execution of the method under test
    var result = aggregate.PerformAction(parameters);

    // Verification of the outcome
    Assert.NotNull(result);
    Assert.Equal(expectedValue, result.Value);
}
```

### Domain Test Categories

* **Aggregate Tests**: Business rule validation and state changes.
* **Value Object Tests**: Immutability and equality.
* **Domain Service Tests**: Complex business operations.
* **Event Tests**: Event publishing and handling.
* **Application Service Tests**: Orchestration and input validation.

### Test Validation Process (MANDATORY)

**Before writing any test, you MUST:**

1.  **Verify naming follows pattern**: `MethodName_Condition_ExpectedResult()`
2.  **Confirm test category**: Which type of test (Unit/Integration/Acceptance).
3.  **Check domain alignment**: Test validates actual business rules.
4.  **Review edge cases**: Includes error scenarios and boundary conditions.

## Quality Checklist

**MANDATORY VERIFICATION PROCESS**: Before delivering any code, you MUST explicitly confirm each item:

### Domain Design Validation

* **Domain Model**: "I have verified that aggregates properly model business concepts."
* **Ubiquitous Language**: "I have confirmed consistent terminology throughout the codebase."
* **SOLID Principles Adherence**: "I have verified the design follows SOLID principles."
* **Business Rules**: "I have validated that domain logic is encapsulated in aggregates."
* **Event Handling**: "I have confirmed domain events are properly published and handled."

### Implementation Quality Validation

* **Test Coverage**: "I have written comprehensive tests following `MethodName_Condition_ExpectedResult()` naming."
* **Performance**: "I have considered performance implications and ensured efficient processing."
* **Security**: "I have implemented authorization at aggregate boundaries."
* **Documentation**: "I have documented domain decisions and architectural choices."
* **.NET Best Practices**: "I have followed .NET best practices for async, DI, and error handling."

### Financial Domain Validation

* **Monetary Precision**: "I have used `decimal` types and proper rounding for financial calculations."
* **Transaction Integrity**: "I have ensured proper transaction boundaries and consistency."
* **Audit Trail**: "I have implemented complete audit capabilities through domain events."
* **Compliance**: "I have addressed PCI-DSS, SOX, and LGPD requirements."

**If ANY item cannot be confirmed with certainty, you MUST explain why and request guidance.**

### Monetary Values

* Use `decimal` type for all monetary calculations.
* Implement currency-aware value objects.
* Handle rounding according to financial standards.
* Maintain precision throughout calculation chains.

### Transaction Processing

* Implement proper saga patterns for distributed transactions.
* Use domain events for eventual consistency.
* Maintain strong consistency within aggregate boundaries.
* Implement compensation patterns for rollback scenarios.

### Audit and Compliance

* Capture all financial operations as domain events.
* Implement immutable audit trails.
* Design aggregates to support regulatory reporting.
* Maintain data lineage for compliance audits.

### Financial Calculations

* Encapsulate calculation logic in domain services.
* Implement proper validation for financial rules.
* Use specifications for complex business criteria.
* Maintain calculation history for audit purposes.

### Platform Integration

* Use system standard DDD libraries and frameworks.
* Implement proper bounded context integration.
* Maintain backward compatibility in public contracts.
* Use domain events for cross-context communication.

**Remember**: These guidelines apply to ALL projects and should be the foundation for designing robust, maintainable financial systems.

---

## NextAurora Platform — Specific Rules & Conventions

These rules are derived from the actual codebase and must be followed for all changes to this solution.

### Project Structure

Every service uses Clean Architecture with four layers:

```
ServiceName.Domain/          # Entities, value objects, interfaces — no dependencies
ServiceName.Application/     # Commands, queries, handlers, validators, behaviors
ServiceName.Infrastructure/  # EF Core, repositories, Service Bus, messaging
ServiceName.Api/             # Endpoints, middleware, DI composition root
```

Layer dependency rule: **Domain → nothing. Application → Domain only. Infrastructure → Domain + Application. Api → all layers.**

### ServiceDefaults

`NextAurora.ServiceDefaults` is the cross-cutting shared library for all services. All shared middleware, telemetry configuration, health checks, and exception handling live here.

- Every service calls `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()` — never bypass these.
- `MapDefaultEndpoints()` registers `CorrelationIdMiddleware`, the global exception handler, and health check endpoints.
- Health check endpoints (`/health`, `/alive`) are active in **all environments**, not just development.

### Package Management

This solution uses **Central Package Management**. All package versions are declared in `Directory.Packages.props`. Individual `.csproj` files reference packages **without version attributes**. Never add a `Version=` attribute to a `<PackageReference>`.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="MediatR" Version="12.4.1" />

<!-- ServiceName.Application.csproj -->
<PackageReference Include="MediatR" />              <!-- ✅ correct -->
<PackageReference Include="MediatR" Version="12.4.1" />  <!-- ❌ wrong -->
```

### C# / .NET Coding Standards

- Target: **.NET 10 / C# 13**
- Use **file-scoped namespaces** (`namespace Foo.Bar;`)
- Private fields prefixed with `_`
- Async methods suffixed with `Async`
- Interfaces prefixed with `I`
- Use `var` when the type is apparent from the right-hand side
- **`TreatWarningsAsErrors` is enabled** — the build fails on any warning; zero warnings are acceptable

### Static Analyzer Compliance (MANDATORY)

Three analyzers run at error severity: **Meziantou**, **SonarAnalyzer**, and **Roslynator**.

Key rules to know:

| Rule | Description | Fix |
|------|-------------|-----|
| MA0002 | `Dictionary<string,T>` created without `IEqualityComparer` | Always pass `StringComparer.Ordinal` |
| S2139 | Exception logged then rethrown bare | Use a `finally` block for timing; never catch-log-rethrow |

**MA0002 pattern (logging scopes):**
```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["CorrelationId"] = correlationId,
    ["MessageId"]     = args.Message.MessageId
});
```

**S2139 pattern (pipeline behaviors — never catch+log+rethrow):**
```csharp
var succeeded = false;
try
{
    var response = await next();
    succeeded = true;
    return response;
}
finally
{
    sw.Stop();
    if (succeeded) logger.LogInformation("Handled {Name} in {Ms}ms", ...);
    else           logger.LogWarning("Failed {Name} after {Ms}ms", ...);
}
```

### MediatR Pipeline Order

Register pipeline behaviors in this order in each service's `Program.cs`:

```csharp
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
```

`ValidationBehavior` runs first (rejects invalid commands before handlers run). `LoggingBehavior` times handler execution and logs correlation ID. Each service Application project must include `Microsoft.Extensions.Logging.Abstractions` for `ILogger` in behaviors.

### Observability Rules

#### Correlation ID

Every HTTP request gets an `X-Correlation-Id` (generated if absent) via `CorrelationIdMiddleware`. The ID propagates through distributed traces via `Activity` baggage.

Read the correlation ID in any handler or behavior:
```csharp
var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
                 ?? Activity.Current?.TraceId.ToString();
```

#### Service Bus Publishing

All publishers must inject the correlation ID into outbound messages:
```csharp
var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
    ?? Activity.Current?.TraceId.ToString();

var message = new ServiceBusMessage(body)
{
    CorrelationId = correlationId
};
if (correlationId is not null)
    message.ApplicationProperties["X-Correlation-Id"] = correlationId;
```

#### Service Bus Consuming — Required Pattern

Every processor message handler **must**:

1. Extract correlation ID from `ApplicationProperties["X-Correlation-Id"]`
2. Open a structured logging scope before dispatching
3. Call `AbandonMessageAsync` on failure — **never silently swallow exceptions**
4. Log `EntityPath`, `ErrorSource`, `FullyQualifiedNamespace` in `ProcessErrorAsync`

```csharp
processor.ProcessMessageAsync += async args =>
{
    var correlationId = args.Message.ApplicationProperties.TryGetValue("X-Correlation-Id", out var cid)
        ? cid?.ToString() : args.Message.CorrelationId;

    using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["CorrelationId"] = correlationId,
        ["MessageId"]     = args.Message.MessageId,
        ["Subject"]       = args.Message.Subject,
        ["DeliveryCount"] = args.Message.DeliveryCount
    });

    try
    {
        // deserialize → dispatch via mediator
        await args.CompleteMessageAsync(args.Message, stoppingToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to process {Subject}. Abandoning for retry/DLQ", args.Message.Subject);
        await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
    }
};

processor.ProcessErrorAsync += args =>
{
    logger.LogError(args.Exception,
        "Service Bus transport error on {EntityPath} (source: {ErrorSource}, namespace: {Namespace})",
        args.EntityPath, args.ErrorSource, args.FullyQualifiedNamespace);
    return Task.CompletedTask;
};
```

#### OpenTelemetry

`ServiceDefaults` registers these sources — do not re-register them:
- `{ApplicationName}` (per-service custom spans)
- `Azure.Messaging.ServiceBus` (Azure SDK spans, automatic)
- Meter name: `"NextAurora"`

### Business Metrics

Use the `System.Diagnostics.Metrics` BCL directly in Application handlers. Do **not** reference `NovaCraftMetrics` from `ServiceDefaults` in the Application layer (that would violate the layer dependency rule).

```csharp
// Static readonly field in the handler class
private static readonly Counter<long> OrdersPlaced =
    new Meter("NextAurora").CreateCounter<long>("orders.placed");

// After the domain action succeeds
OrdersPlaced.Add(1);
// With tags when relevant:
PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
```

Defined counters: `orders.placed`, `payments.processed` (tag: `outcome`), `shipments.dispatched`, `notifications.sent` (tag: `channel`).

### Database Health Checks

Every service with a `DbContext` must register a health check in its Infrastructure `DependencyInjection.cs`:

```csharp
services.AddDbContext<OrderDbContext>(...);
services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>();
```

The `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` package must be referenced (without version) in the Infrastructure `.csproj`.

### Exception Handling

`GlobalExceptionHandler` (in `ServiceDefaults`) maps exceptions to `ProblemDetails`:

| Exception | HTTP Status |
|-----------|-------------|
| `FluentValidation.ValidationException` | 400 — includes per-field errors |
| `ArgumentException` | 400 |
| `InvalidOperationException` | 409 |
| Anything else | 500 |

All error responses include `traceId`. Never expose stack traces, internal IDs, or connection strings in API responses.

### Commands & Queries

- Commands return `Guid` (the created entity's ID)
- Queries return DTOs — never domain entities
- Every command must have a `FluentValidation` validator in the Application layer

### Domain Entities

- Use static `Create()` factory methods with validation — no public constructors
- All state changes through domain methods — no public property setters
- Aggregate roots control access to child collections — expose `Add*()`/`Remove*()` methods, never raw `List<T>`

---

## CRITICAL REMINDERS

**YOU MUST ALWAYS:**

* Show your thinking process before implementing.
* Explicitly validate against these guidelines.
* Use the mandatory verification statements.
* Follow the `MethodName_Condition_ExpectedResult()` test naming pattern.
* Confirm financial domain considerations are addressed.
* Stop and ask for clarification if any guideline is unclear.

**FAILURE TO FOLLOW THIS PROCESS IS UNACCEPTABLE** - The user expects rigorous adherence to these guidelines and code standards.
## Observability & Context Propagation

### Three context identifiers

| Concept | Activity Baggage Key | HTTP / SB Property | Logger Scope Key |
|---------|--------------------|--------------------|-----------------|
| Correlation | `correlation.id` | `X-Correlation-Id` | `CorrelationId` |
| User | `user.id` | `X-User-Id` | `UserId` |
| Session | `session.id` | `X-Session-Id` | `SessionId` |

These are populated at two entry points:
- **HTTP**: `CorrelationIdMiddleware` — extracts `correlation.id` from header, `user.id` from JWT `sub` claim, `session.id` from `X-Session-Id` header
- **Service Bus**: Each processor handler — extracts all three from `ApplicationProperties`

And propagated in `ServiceBusEventPublisher` from `Activity.Current?.GetBaggageItem()` into outgoing message `ApplicationProperties`.

### LoggingBehavior must open BeginScope

`LoggingBehavior` must call `logger.BeginScope(dict)` before `await next()` so handler log lines inherit context. The dict must use `StringComparer.Ordinal` (MA0002) and must only include keys where the value is non-null.

```csharp
var scopeState = new Dictionary<string, object?>(StringComparer.Ordinal) { ["CorrelationId"] = correlationId };
if (userId is not null) scopeState["UserId"] = userId;
if (sessionId is not null) scopeState["SessionId"] = sessionId;
using (logger.BeginScope(scopeState)) { ... }
```

### LoggingEventPublisher (decorator)

Each service that publishes events wraps `ServiceBusEventPublisher` with `LoggingEventPublisher`. Register as:
```csharp
services.AddScoped<ServiceBusEventPublisher>();
services.AddScoped<IEventPublisher, LoggingEventPublisher>();
```
`LoggingEventPublisher` injects `ServiceBusEventPublisher` directly (not `IEventPublisher`) to avoid circular dependency.

### Admin Event Endpoints

Protected by `AdminKeyEndpointFilter` (checks `X-Admin-Key` header vs `AdminApiKey` config). Returns **403** if `AdminApiKey` is not configured, **401** if key is wrong (fail-closed).

### Analyzer Rules (reminders)

- **MA0002**: `new Dictionary<string, object?>(StringComparer.Ordinal)` — always pass comparer
- **S2139**: No catch-log-rethrow; use `finally` + `bool succeeded` pattern
- **S108**: No empty catch blocks — always put `return null;` or a log inside
