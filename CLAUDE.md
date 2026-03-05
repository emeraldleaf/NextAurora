# NextAurora - Claude Code Project Instructions

## Project Overview

NextAurora is a .NET 10 microservices e-commerce platform using Aspire, Azure Service Bus, gRPC, EF Core, and Blazor. It follows DDD, CQRS, and event-driven architecture.

## Architecture Principles

### SOLID

- **Single Responsibility**: Each class has one reason to change. Handlers handle one command/query. Processors handle one event type. Do not mix concerns.
- **Open/Closed**: Use abstractions (interfaces, base classes) so behavior can be extended without modifying existing code. New event types = new handler classes, not new branches in existing processors.
- **Liskov Substitution**: All interface implementations must fully honor the contract. Repository implementations must handle all methods.
- **Interface Segregation**: Keep interfaces focused. Separate read/write repository interfaces if consumers only need one. Do not force unused dependencies.
- **Dependency Inversion**: Always depend on abstractions (interfaces), never on concrete implementations. Domain and Application layers must never reference Infrastructure.

### Domain-Driven Design

- **Rich Domain Entities**: Entities must enforce their own invariants. All state changes go through methods, never through public setters. Use factory methods (static `Create()`) with validation.
- **Value Objects**: Use value objects for concepts like Money (amount + currency), Quantity (non-negative int). They enforce rules at construction.
- **Aggregates**: Each aggregate root controls access to its children. Do not expose mutable collections. Add methods like `AddLine()` instead of exposing `List<T>`.
- **Domain Events**: State changes that affect other bounded contexts should raise domain events.
- **Layer Dependencies**: Domain -> nothing. Application -> Domain. Infrastructure -> Domain + Application. Api -> all layers (composition root).

### Security Requirements

- **Authentication**: All non-public endpoints must use `.RequireAuthorization()`. JWT Bearer authentication.
- **Authorization**: Users can only access their own resources. Validate `buyerId` matches authenticated user.
- **Input Validation**: All commands must have FluentValidation validators. Validate at the API boundary before reaching handlers.
- **Error Handling**: Never expose internal state, stack traces, or entity IDs in API responses. Log details server-side, return generic errors with correlation IDs to clients.
- **HTTPS**: Enforce HTTPS redirection in production.
- **CORS**: Explicit CORS policy allowing only known frontend origins.
- **Rate Limiting**: Applied to search and payment endpoints at minimum.

## Project Structure

Each service follows Clean Architecture:

```
ServiceName/
  ServiceName.Domain/          # Entities, value objects, enums, interfaces (no dependencies)
  ServiceName.Application/     # Commands, queries, validators, handlers (depends on Domain only)
  ServiceName.Infrastructure/  # EF Core, repositories, messaging (depends on Domain + Application)
  ServiceName.Api/             # Endpoints, middleware, DI composition root (depends on all)
```

## Coding Standards

- .NET 10 / C# 13
- File-scoped namespaces
- Private fields prefixed with `_`
- Async methods suffixed with `Async`
- Interfaces prefixed with `I`
- Use `var` when type is apparent
- TreatWarningsAsErrors is enabled - zero warnings allowed
- Static analyzers: Meziantou, SonarAnalyzer, Roslynator

## Package Management

- Central Package Management via `Directory.Packages.props` - all versions defined there
- Individual `.csproj` files reference packages WITHOUT version attributes
- Shared build settings in `Directory.Build.props`

## Communication Patterns

- **Async events** (Azure Service Bus): For workflow orchestration (order -> payment -> shipping -> notification)
- **gRPC** (sync): For real-time queries between services (OrderService -> CatalogService product validation)
- **REST** (HTTP): For frontend-to-service communication only

## Key Conventions

- Commands return the created entity's ID (Guid)
- Queries return DTOs, never domain entities
- Domain entities use factory methods (`Create()`) with validation, not public constructors
- Event handlers must be idempotent
- Use the Outbox pattern for guaranteed event publishing (save entity + event in same transaction)
- All API responses should use Result pattern or ProblemDetails for errors
- Never commit .env files, connection strings, or secrets

## Testing

- Unit tests for domain logic and handlers
- Integration tests with Testcontainers for infrastructure
- Run `dotnet build` to verify - all analyzer warnings are errors

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project NextAurora.AppHost  # Starts everything via Aspire
```
