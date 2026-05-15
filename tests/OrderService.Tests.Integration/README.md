# OrderService.Tests.Integration

Integration tests that boot the **real** OrderService API in-process (via
`WebApplicationFactory<Program>`) against a **real SQL Server container**, with Wolverine's
external transports stubbed so the saga handlers run locally and the transactional outbox is
exercised against the real DB.

Unit tests (`OrderService.Tests.Unit`) substitute the repository, the bus, and the DB. These
tests don't — they exercise the seams that only break against the real stack:

| Test | What it proves |
|---|---|
| `PlaceOrder_persists_order_and_publishes_OrderPlacedEvent` | The Wolverine middleware chain (FluentValidation → ContextPropagation → AutoApplyTransactions → handler) runs end-to-end, the Order row is committed, and the `OrderPlacedEvent` is published through Wolverine's pipeline (which `UseDurableOutboxOnAllSendingEndpoints` wraps with same-transaction outbox staging). |
| `PlaceOrder_does_not_persist_when_catalog_validation_fails` | A failure before the `SaveChanges` leaves no Order row — no partial-state surprise. |
| `PaymentCompletedEvent_transitions_Placed_to_Paid_and_is_idempotent` | The saga consume-side: the event routes to `PaymentCompletedHandler`, the Order transitions correctly, and a redelivery of the same event no-ops (the application-layer idempotency guard + domain-method guard work over a real DB). |
| `Order_RowVersion_token_rejects_concurrent_write` | SQL Server's `rowversion` concurrency token actually throws `DbUpdateConcurrencyException` when two scopes race to mutate the same Order. |

## Running locally

You need **Docker running** with enough headroom for a SQL Server container (~2 GB RAM). Then:

```bash
dotnet test tests/OrderService.Tests.Integration/OrderService.Tests.Integration.csproj
```

First run pulls `mcr.microsoft.com/mssql/server:2022-latest` (~1.5 GB on the wire, ~2.3 GB on
disk). Subsequent runs reuse the cached image; container startup is ~10–15 s. SQL Server is
hungry — if Docker is already running several heavy containers, expect either slow starts or
OOM exits (`(255)`). Free some memory or close other containers if you see that.

### macOS Docker Desktop gotcha

Same as the CatalogService slice — Docker Desktop's socket is at `~/.docker/run/docker.sock`,
not the standard `/var/run/docker.sock`. Testcontainers needs `DOCKER_HOST` pointed there:

```bash
DOCKER_HOST="unix://$HOME/.docker/run/docker.sock" \
  dotnet test tests/OrderService.Tests.Integration/OrderService.Tests.Integration.csproj
```

CI (`ubuntu-latest`) ships Docker at the standard path, so the `integration-tests` job in
`.github/workflows/ci.yml` needs no override.

## Structure

- **`OrderApiFactory`** — `WebApplicationFactory<Program>` + `IAsyncLifetime`. Starts the
  SQL Server container, injects its connection string as both `orders-db` and the persistence
  endpoint for Wolverine's outbox tables (`wolverine` schema, auto-created at startup via
  `AddResourceSetupOnStartup`). A syntactically-valid Azure Service Bus connection string is
  also injected — never used over the wire, but `UseAzureServiceBus(...)` parses it eagerly.
  Calls `services.DisableAllExternalWolverineTransports()` so outgoing messages route to
  in-process stubs while the durable outbox still wraps them.
- **`TestAuthHandler`** — always-succeeds auth. The handler stamps a fixed buyer Guid into the
  `NameIdentifier` claim so the endpoint's buyer-scope check passes when tests POST orders.
- **`OrderSagaTests`** — the tests. Test 1 uses Wolverine's `TrackActivity().ExecuteAndWaitAsync`
  to wait on the pipeline and assert on the published event. Tests 3/4 use
  `PublishMessageAndWaitAsync` to drive the saga consume-side. Test 5 hits the EF concurrency
  path directly via two DbContext scopes.

## Why stubbed transport instead of the Azure Service Bus emulator

The outbox-staging guarantee (entity-write + envelope-write same transaction) and the saga
consume-side handlers are what this slice proves — the ASB wire path itself mostly exercises
Microsoft's emulator + Wolverine's transport adapter, which is the fragile last mile and
lower-value per unit of effort. The ASB-emulator-based wire test is filed as a separate
follow-up in [docs/STATUS.md](../../docs/STATUS.md).

## Adding more

This is the second integration slice (after CatalogService) and the proven pattern for any
service that has a DB + Wolverine handlers. Payment and Shipping would follow the same shape;
the saga *across* services (real cross-service choreography) needs all services booted plus
either the ASB emulator or coordinated in-process Wolverine — a separate, heavier project.
