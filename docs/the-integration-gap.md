# The Integration Gap

> Article draft / session retrospective. Captures the cascade of integration failures
> surfaced when the full NextAurora Aspire stack was run end-to-end locally for the first
> time (2026-06-13). Source material for a blog post; every claim below was hit and verified
> in a real debugging session. Tracking issue for the open piece: **#148**.

## Thesis

**"Built and fully tested" is not the same as "runs end-to-end on a laptop."**

Every NextAurora service passed its unit + Testcontainers integration tests. CI was green.
The architecture was sound. Yet the *full Aspire stack had never once run end-to-end
locally* — each service had only ever been exercised in isolation, with stubbed transports
or single-service Testcontainers. The moment we composed the whole thing, a cascade of
**seven distinct integration gaps** surfaced — every one invisible to per-service testing
because it lived in the **seams**: service discovery, emulator fidelity, identity config,
port assignment, transport negotiation, broker management.

The gap between "my tests pass" and "my system runs" is exactly the set of assumptions no
single test can hold.

## The cascade (each a mini-section)

1. **The environment lied first.** A disk-full event corrupted Docker's containerd metadata
   DB (`write …/meta.db: input/output error`, surfacing as random daemon wedges); a forgotten
   `~/.local/bin/dotnet` wrapper silently blocked ASP.NET dev-cert trust. *Lesson: distrust the
   substrate before the code.*

2. **Emulator ≠ the real thing (part 1).** Aspire 13's Azure Service Bus emulator has no
   management API; Wolverine's `AutoProvision` (which uses it) crash-looped every messaging
   service on startup with `BrokerInitializationException` — while the one non-messaging service
   (Catalog) stayed up, making the failure look selective and sending early debugging toward a
   dev-cert red herring.

3. **Nobody had assigned the services a port.** Four services shipped without `launchSettings`,
   so they all defaulted to `http://localhost:5000`, collided with each other *and* with macOS
   AirPlay Receiver (which squats on `:5000`/`:7000`).

4. **Auth worked in theory, not in wiring.** The API read `Keycloak:Url`; the Aspire Keycloak
   integration injects `Keycloak:AuthServerUrl` + `Keycloak:Realm`. No authority resolved → no
   JWT bearer scheme registered → `.RequireAuthorization()` threw on the challenge → every
   authed call returned **409, not 401**. Then a chain of follow-ons: missing `sub` (Keycloak 25
   moved it into the `basic` client scope, which the client didn't request), missing `aud` (no
   audience mapper), an invalid redirect-URI wildcard (`http://localhost:*/*` — Keycloak only
   allows wildcards at the *end*), and React 19 StrictMode double-spending the single-use OAuth
   authorization code.

5. **gRPC had no transport.** Aspire's `https+http://` service-discovery scheme isn't resolvable
   by `Grpc.Net.Client`'s channel (the `.AddServiceDiscovery()` extension wires the *HttpClient
   handler*, not the channel's Balancer resolver), and the catalog service had no HTTPS endpoint
   — so there was literally no HTTP/2 path for the order→catalog validation call. Checkout 409'd.

6. **Emulator ≠ the real thing (part 2 — the still-open issue, #148).** Past provisioning,
   Wolverine *still* calls the management API to "set up" (EnsureExists) every topic/queue it
   touches — including the application topics and its own system queues (response/retries/
   dead-letter). Against the emulator these all fail, so the saga's publish silently never
   happens. The order persists and returns 202; the `OrderPlaced → Paid → Shipped` choreography
   never starts.

7. **(Meta) Each fix revealed the next.** Classic onion. The discipline that mattered most was
   knowing when to *stop and ship the working layer* (buyer checkout) versus chase the next.

## The technical centerpiece (the most reusable lesson)

**When you target an emulator, you inherit its *capability surface*, not just its API.**

The Azure Service Bus emulator speaks AMQP (send/receive) but does not serve the administration
REST API (create/verify topology) on the connection string consumers use. Any library that
"helpfully" provisions or verifies infrastructure at startup — Wolverine, MassTransit,
NServiceBus all do variants — will fail against it.

The fix is a **two-mode configuration**, gated on one flag the orchestrator flips per
environment:

- **Real Azure:** let the library provision and verify (the management API is there).
- **Emulator / local:** declare the topology *out of band* (Aspire writes the emulator's
  config file from `AddServiceBusTopic`/`AddServiceBusSubscription`), and tell the library to
  **skip every management operation** and just connect over the data plane.

In Wolverine terms, the emulator branch is: `AutoProvision` off, `SystemQueuesAreEnabled(false)`,
skip `AddResourceSetupOnStartup`, and build the SQL outbox store via
`AutoBuildMessageStorageOnStartup` instead. All gated on `Wolverine:AutoProvision`, which the
AppHost injects as `false` for the emulator and leaves `true` for Publish mode.

### The twist: necessary but not sufficient

That config is *correct* — it removes every management-API error from the logs and the services
boot cleanly. But it does **not** make the saga flow. Tracing it down to the outbox revealed the
deeper truth: with management disabled, Wolverine started only its `stub://replies/` and
`dbcontrol://` listeners — **no `asb://` listeners or senders established**. A published
`OrderPlacedEvent` therefore found no route and was never even staged in the outbox (row count:
0). The order persists and returns 202; the event simply has nowhere to go.

The root insight is sharper than "disable provisioning": **Wolverine's Azure Service Bus
transport won't bring its endpoints up unless it can complete management-API setup
(`EnsureExists`) against the broker** — and the emulator can't serve that on the AMQP connection
string. So the saga over the emulator has *never* worked, with or without resource setup; one
path floods the logs with errors, the other goes quiet but is equally inert.

The genuinely clean fix is Wolverine's `ManagementConnectionString` pointed at the emulator's
HTTP management port (`EMULATOR_HTTP_PORT=5300`). The blocker is environmental, not conceptual:
Aspire's `RunAsEmulator()` only proxies the AMQP port to host-process services, and the ports are
dynamic — so there's no injected management endpoint to point at. Closing that is an AppHost +
Aspire-wiring task, and it's contingent on whether the emulator's 5300 actually implements the
administration REST API the Azure SDK expects. That investigation is the remaining work on #148.

**The reusable lesson holds and sharpens:** an emulator's *capability surface* isn't just "which
API verbs exist" — it's whether your framework's **startup contract** (here: provision-or-verify
before listen) can be satisfied at all. A library that treats the broker as something it
*manages* rather than merely *connects to* may be fundamentally incompatible with a
data-plane-only emulator, no matter how many flags you flip.

## Takeaways (what to do Monday)

1. **Add an end-to-end smoke run to the local/CI ritual, not just per-service tests.** The
   integration gap only closes when you *compose the whole thing*.
2. **Emulators are capability-partial.** Know which plane (data vs. management) an emulator
   implements before you point an opinionated framework at one.
3. **Service-discovery schemes are transport-specific.** `https+http://` works for `HttpClient`,
   not for a gRPC channel's resolver. Resolve to a concrete endpoint when in doubt.
4. **"Works in isolation" hides cross-cutting config** — identity keys, port assignment, claim
   shapes, transport protocols — that only the full wiring exercises.
5. **Ship the layer that works; scope the rest.** Buyer checkout shipped (PRs #144/#146/#149/
   #151); the saga-over-emulator (#148) is a contained, documented follow-up.

## Status at time of writing

- ✅ Buyer checkout works end-to-end locally (login → cart → place order → my orders), verified.
- 🔬 #148 (saga progression over the emulator) — root cause fully characterized (above): Wolverine's
  ASB transport won't establish endpoints without a management API the emulator can't serve on the
  AMQP connection string. The two-mode config (errors-off) was prototyped and **reverted** — it's
  necessary but insufficient, so it doesn't belong on `main` until paired with the
  `ManagementConnectionString` → port-5300 work that actually brings the endpoints up. Tracked on #148.
- ✅ Saga *logic* is covered by integration tests (stubbed Wolverine transport), so this is a
  local-emulator-fidelity gap, not a correctness gap.
