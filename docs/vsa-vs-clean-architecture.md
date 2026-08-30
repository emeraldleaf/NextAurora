# VSA vs. Clean Architecture — a decision guide

> **Portable reference, not NextAurora-specific.** A reusable guide for the
> recurring "Vertical Slice vs. Clean Architecture" decision on any .NET (or
> similar) project. The short version: they're not competitors, the choice is
> mostly about *enforcement mechanism*, and modern testing tools have shifted
> the cost/benefit. NextAurora appears at the end as the worked example. Lift
> this doc into another repo and the guide still applies.

---

## The reframe: "Clean Architecture" is two different things

Most VSA-vs-Clean debates are confused because **two separate things wear the
name "Clean Architecture"**, and only one of them actually conflicts with VSA:

1. **The dependency rule** — source-code dependencies point *inward*; the domain
   depends on nothing (no EF, no web framework, no IO); infrastructure sits at
   the edges. This is the *principle*. It is **not** earned, **not**
   complexity-gated, and **not** in tension with VSA. Decoupling business logic
   from frameworks is worth it at any size.
2. **The multi-project layered structure** — separate `Domain` / `Application` /
   `Infrastructure` / `Api` csproj projects, with the dependency rule enforced at
   *compile time* by project references. This is the *ceremony*. It's the only
   part with a real cost, and the only part the "is it worth it?" question is
   actually about.

Keep these straight and the whole debate clarifies: **you almost always want #1;
you rarely need #2.**

## VSA and Clean are orthogonal axes

They answer different questions, so you apply both at once:

- **VSA answers "where does code live?"** → organized by *feature* (one slice per
  use case: endpoint + validator + handler + data access + DTOs, co-located).
  This is about **cohesion**.
- **Clean (the dependency rule) answers "which way do dependencies point?"** →
  inward, toward the domain. This is about **decoupling**.

You can have great organization (clean VSA folders) with terrible dependency
hygiene (domain importing EF everywhere), or vice versa. They're independent.
"Combine them" — the common advice — just means: **organize by feature (VSA) and
keep the domain decoupled (Clean's dependency rule)**, on their respective axes.

## VSA does *not* enforce the dependency rule automatically — you enforce it

A common overstatement is "VSA naturally achieves dependency inversion." It
doesn't. VSA is *only* file organization; nothing about grouping code by feature
stops a slice from mashing domain logic + EF + HTTP together. The dependency rule
is something you **apply** (depend on abstractions, keep the domain a plain
object) and **enforce** by *some* mechanism. The mechanism is the real decision,
and it's a spectrum — escalate only as the cost of a violated boundary rises:

| Rung | Mechanism | What it costs | When |
|---|---|---|---|
| 1 | **Convention** (+ code review) | Discipline; a bad PR can slip through | Small team, low churn, trusted contributors |
| 2 | **Architecture tests** (NetArchTest / ArchUnitNET / Roslyn analyzer) | One test project; runs in CI | You want the boundary enforced deterministically without ceremony — **the rung most teams skip** |
| 3 | **Project split** (the 4-project structure) | 4 csprojs, the layer dance, indirection | You want the *compiler* (not a test) to hold the line, OR you need separate deploy/versioning units |

**The key insight:** rung 2 gives you the *same* boundary enforcement as the
4-project split — "Domain references no EF/Infrastructure" fails CI — **without
splitting into projects.** An architecture test works at the namespace level in a
single-project VSA codebase:

```csharp
Types.InAssembly(typeof(Order).Assembly)
     .That().ResideInNamespace("MyService.Domain")
     .ShouldNot().HaveDependencyOnAny(
         "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "MyService.Infrastructure")
     .GetResult().IsSuccessful.Should().BeTrue();
```

So the 4-project split is rarely needed *for enforcement alone*. Reach for it
only when domain complexity makes you want compile-time guarantees, or when the
projects are genuinely separate deploy/versioning units.

## The two meanings of "combine"

"Combine Clean + VSA" is used for two compatible things:

- **(A) Orthogonal-axes combine** — VSA for organization + the dependency rule
  for direction, applied across the whole codebase (above).
- **(B) Per-slice complexity gradient** — each slice pulls in only as much
  layering as it needs. A simple read is a flat `query → DTO`; a complex write
  routes through a rich aggregate with invariants and value objects. Same
  service, different depth per slice. The complex 20% look "Clean-ish" inside;
  the simple 80% stay flat.

What "combine" does **not** mean: mixing Clean-layered and VSA services in one
solution (some services as 4 projects, others as slices). That's not
combination, it's inconsistency — pick one shape per service.

## How modern testing shifted the calculus

This is the part that changed in the last few years and undercuts a major
historical argument *for* the layered structure.

The old pro-layering argument: "separate projects + repository interfaces let me
unit-test business logic with everything mocked." But for code that touches a
database, **mock-heavy unit tests test the wiring, not the behavior** — they
can't catch a bad SQL query, a schema constraint, a concurrency-token race, or a
cartesian-row blowup. **Testcontainers** changed this: spinning up a real
database/broker/cache in a disposable container is now nearly as easy as writing
a mock, so the highest-value test for a slice is an *integration test that
exercises the whole slice against real infrastructure.*

Consequences:
- **Integration-default for IO-touching code.** Handlers that hit a DB belong in
  integration tests (real DB via Testcontainers), not mocked-repository unit
  tests. This is higher ROI: it tests the contract and the actual behavior.
- **The repository-interface-for-testability argument collapses.** A big reason
  people added `IRepository` wrappers was to mock them in unit tests. If the
  right test is an integration test against a real DB, the wrapper buys nothing —
  `DbContext` is already the Unit of Work and `DbSet<T>` is already the
  Repository. (This is also why "VSA with a repository interface per slice," a
  common example, is often *more* ceremony than you need on top of EF Core.)
- **Testing Trophy, not a pure rectangle.** Don't over-rotate to
  "integration-everything." Keep fast, Docker-free **unit tests for *pure* domain
  logic** — aggregate invariants, value-object rules, validators, idempotency
  guards. You don't spin up a container to assert `Price <= 0 throws`. The trophy
  has a real unit base for pure logic + a fat integration middle for slices.
- **The cost to remember:** integration tests need Docker and are slower
  (container spin-up). That's precisely why pure-logic unit tests keep their
  place — fast and dependency-free.

## The duplication tradeoff (VSA's real cost)

VSA's honest cost is **code duplication across slices** — and "refactoring
discipline" to manage it. Two things make this manageable:
- **Keep cross-cutting concerns out of the slices.** Validation, logging,
  auth, transactions/outbox, telemetry belong in *pipeline behaviors /
  middleware*, applied once — not copy-pasted per slice. Done right, "duplication
  across slices" stays shallow.
- **Prefer "duplication over the wrong abstraction."** If two features persist
  data slightly differently, two explicit handlers beat one brittle shared
  service. Repeated *shapes* (a projection pattern, an ownership predicate) that
  carry per-slice meaning are kept on purpose — abstracting them often hides a
  read contract or weakens a security boundary.

**Duplication cost scales with slice count**, and it bites hardest on **pure
CRUD** — 80 near-identical validate→save→return slices is real waste with no
isolation benefit, because there's no per-slice complexity to isolate. For pure
CRUD, a generic/scaffolded/convention approach beats VSA. But "pure CRUD" is rarer
than it sounds: the moment you add per-operation validation, ownership checks, or
a complex 20%, VSA's "keep the simple slices simple, isolate the complex ones"
starts paying off.

## When to use which

| Situation | Shape |
|---|---|
| Default for most services (≤ ~4 aggregates, mixed simple+complex use cases) | **VSA**, dependency rule enforced by convention + review |
| "I want the dependency rule enforced deterministically" | **VSA + architecture tests** (NetArchTest) — not a project split |
| Genuinely pure CRUD, uniform, no logic | Neither shines — generic/scaffolded endpoints, OData, or thin minimal-API + EF |
| 5+ aggregates with cross-cutting domain rules several features coordinate on; you want the *compiler* to hold the boundary; or separate deploy/versioning units | **Multi-project split** (the heavyweight Clean structure) |
| "I want to mock the DbContext in unit tests" | NOT a reason for any of the above — use integration tests with Testcontainers |

## Common mistakes

- **Treating it as "VSA *or* Clean."** They're orthogonal — you combine the
  dependency rule (Clean) with feature organization (VSA).
- **Reaching for the 4-project split to get the dependency rule.** An
  architecture test gives the same enforcement without the ceremony.
- **Adding a repository interface per slice "for testability / DB independence."**
  On EF Core that's usually a layer without capability; `DbContext`/`DbSet<T>`
  are already UoW + Repository, and the right tests are integration tests.
- **Mixing layered and sliced services in one solution and calling it "best of
  both."** That's inconsistency; pick one shape per service.
- **Over-rotating to integration-everything.** Keep unit tests for pure logic.
- **Applying VSA to pure CRUD** and paying the duplication tax for isolation you
  don't need.

## Worked example — NextAurora

NextAurora is .NET 10 microservices (Order → Payment → Shipping → Notification
saga). It applies this guide as follows:

- **VSA across all five services** — feature folders (`Features/<UseCase>.cs`),
  aggregates in `Domain/`. It originally ran one service on the 4-project Clean
  structure and the rest on VSA, then **collapsed the Clean one to VSA** — at
  ~2k LOC / 2 aggregates the project split never earned its keep, and mixing
  shapes was inconsistency without payoff.
- **Clean's dependency rule kept throughout** — `Domain → nothing` survived the
  collapse to single-project VSA. The *principle* stayed; only the *structure*
  was dropped. Enforced today by convention + an automated architecture-review
  pass + CodeRabbit; **architecture tests (NetArchTest) are the named next rung**
  to make it deterministic without changing the VSA shape.
- **Per-slice gradient** — `GetProductById` is a flat projection (`query → DTO`,
  no aggregate); `PlaceOrder` pulls in the `Order` aggregate, `OrderLine` value
  objects, and domain invariants. Same service, different depth.
- **DbContext directly, no repository wrappers** — handlers take the `DbContext`;
  the five entity-returning `IFooRepository` interfaces were deleted in a
  "simplicity refactor" precisely because their testability justification fails
  once you test EF-touching code with Testcontainers.
- **Integration-default testing** — handler tests run against real Postgres /
  SQL Server / Redis via Testcontainers; unit tests are reserved for pure domain
  logic (aggregate invariants, validators, idempotency guards). (4 of 5 services have
  integration projects; NotificationService is stateless and unit-only.)

Net: **VSA for organization + Clean's dependency rule throughout + the lightest
enforcement that holds + integration-default tests.** The 4-project structure is
a last resort the project explicitly retired, not a default.
