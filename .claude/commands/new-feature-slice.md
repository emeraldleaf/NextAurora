---
description: Scaffold a new VSA feature slice (command + validator + handler) in a service
argument-hint: <ServiceName> <FeatureName>
disable-model-invocation: true
---

# /new-feature-slice

Scaffold a new vertical-slice feature in one of the five VSA services (Order, Payment, Shipping, Notification, Catalog).

## Inputs
$ARGUMENTS — expected as two words: `<ServiceName> <FeatureName>` (e.g. `OrderService CancelOrder`).

## What to do

1. **Validate the service name.** Per CLAUDE.md "Project Structure", VSA applies to all five services — OrderService, PaymentService, ShippingService, NotificationService, and CatalogService. New CatalogService slices go in `CatalogService/Features/` like the rest.

2. **Read the canonical example** at [OrderService/Features/PlaceOrder.cs](../../OrderService/Features/PlaceOrder.cs) to match the existing style:
   - file-scoped namespace
   - `public record {FeatureName}Command(...)` (or `Query`)
   - `public class {FeatureName}CommandValidator : AbstractValidator<...>`
   - `public class {FeatureName}Handler(...)` with primary-constructor DI
   - `HandleAsync` (NOT `Handle`) returning `Task<TResult>` and taking `CancellationToken`
   - Tier-1 teaching comment block at the top of the file explaining the *why* of this slice

3. **Create the file** at `{ServiceName}/Features/{FeatureName}.cs` with a minimally-filled stub:
   - Empty command/query record with a TODO comment naming the inputs to gather from the user
   - Validator with one placeholder rule + a TODO
   - Handler that takes a repository (look up the repo interface in `{ServiceName}/Domain/`) + ILogger + any other ports it'll need, and throws `NotImplementedException` with a one-line TODO

4. **Do NOT** register endpoints, add migrations, or modify other files. This command is *scaffold only* — the user wires it up after reviewing.

5. **Print a follow-up checklist** to stdout listing:
   - the new file path
   - the endpoint registration that's likely needed (and which file in `Endpoints/` it belongs in)
   - whether this feature publishes any events (and if so, the event contract location in `NextAurora.Contracts/Events/`)
   - whether it touches the outbox (yes if it publishes events — wire `eventPublisher.PublishAsync` per the `PlaceOrder` pattern)

## Why this command exists

The five VSA services share an almost-mechanical feature-slice shape. Hand-scaffolding it
risks drift (forgetting `CancellationToken`, calling it `Handle` instead of `HandleAsync`,
missing the Tier-1 comment block). This command bakes the convention into a single
keystroke.
