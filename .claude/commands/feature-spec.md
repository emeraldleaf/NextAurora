---
description: Draft a structured feature spec — goal + acceptance + affected surfaces + auto-referenced CLAUDE.md constraints + handoff to scaffolding. Feeds the encoding loop.
argument-hint: <short feature description, or "pasted">
disable-model-invocation: true
---

# Feature spec

The user is starting work on a new feature. Your job is to produce a structured
spec that captures the *handoff* — goal + acceptance + affected surfaces + which
CLAUDE.md rules the implementation must respect. The spec is ephemeral (it ships
as a GitHub issue + PR description, then archives); the *lessons learned during
implementation* are what get encoded into the durable rule set.

The pattern is: spec → code → encode lessons → smarter next session.

## Inputs

`$ARGUMENTS` is one of:
- A short feature description in plain language → spec it directly
- The literal word `pasted` → the feature description is in the user's previous
  message; spec that
- Empty → ask the user what feature they want to spec

## What to do

### 1. Value gate (before drafting anything)

Three questions to answer honestly **before** the spec is drafted. If the
answers don't land, this isn't a feature spec — it's an experiment, and the
right output is a token budget + a stop-time, not a spec.

1. **Who needs this, and what breaks for them if it never exists?**
   If the honest answer is "no one," this is an experiment. Treat it as one.
2. **Would we still build it if it cost a week of engineering time instead
   of an afternoon of tokens?**
   This is the cost question, asked deliberately now that generation is cheap.
   Most feature inflation will not survive it.
3. **Who owns saying no to this?**
   A decision with no owner is a trap. Name the person whose job is the
   refusal. (For solo work: you. For team work: a named role.)

If question 1 surfaces "no one," propose running it as a time-boxed experiment
(token budget + stop-time) instead of drafting a full spec. If question 3
surfaces "nobody, really," go find the owner first.

Once the value gate passes, lock in the things the spec needs that the user's
one-liner didn't say (2–4 targeted scoping questions):

- Which service(s)? (Catalog / Order / Payment / Shipping / Notification — or new)
- New endpoint, modifying existing behavior, or a saga step?
- New aggregates / state transitions, or operations on existing ones?
- External integrations (gRPC peer, new ASB topic, third-party API)?

Keep questions tight. If the user's description already answers, skip.

### 2. Draft the structured spec

Produce markdown with these sections:

**Goal** — one sentence: the user-facing outcome.

> **Two-implementations test for the goal.** Apply this test: *"Can two
> completely different implementations both satisfy this goal?"* If yes,
> you wrote a goal. If only one implementation could possibly satisfy it,
> you wrote a spec disguised as a goal — and demoted the agent from
> decision-maker to typist. Counter-example: *"Build a Go microservice
> using gRPC, with Postgres for storage and Redis for state, behind an
> Envoy sidecar"* is a spec in goal's clothes. *"Build a microservice
> that handles the user-facing product catalog"* is a goal.

**Acceptance criteria** — bullets, externally observable:
- API contract (request shape, response shape, status codes)
- Saga progression (events fired, events consumed)
- Side effects (DB rows written, events emitted, cache invalidations)
- Failure modes (invalid input, race, downstream unavailable)

> **Constraints vs failure conditions — decision rule.** For each line you're
> about to write, ask: *"Would knowing this change how the builder writes
> code?"* If yes → it's a constraint and goes in the constraints/affects
> section (the builder needs it to make a design call). If no → it's a
> failure condition that goes in acceptance (the validator catches it after
> the code exists). Example: *"Must not introduce a new runtime dependency"*
> → constraint. *"Unit test coverage must stay above 90%"* → failure
> condition. Mixing them invites coverage gaming and reward-hacking.

**Affects** — surfaces this touches:
- New / changed endpoints
- New / changed aggregates
- New / changed events
- New / changed gRPC contracts
- New external dependencies

**Upstream dependencies (assumptions that could shift)** — list the
load-bearing assumptions this spec depends on. If any of these change
mid-build, the spec becomes invalid and the AI should flag it:

- Deployment target (Fly.io / Aspire local / future Hetzner)
- DB engine version (Postgres major bumps, SQL Server provider quirks)
- External API contracts (Stripe, ASB protocol versions)
- Auth model (JWT claim shape, Keycloak realm structure)
- gRPC contracts shared with other services
- CLAUDE.md rules that are themselves in flux (see open `rule-encoding-deferred` issues)

Naming these inline lets the AI flag *during build* if it notices any of them
shifting (e.g., "you bumped EF Core; this spec assumed the old behavior of X").
Closes the failure mode of *"every downstream task inheriting assumptions that
no longer held."*

**Non-functional constraints** (optional — include only when relevant) —
*qualities the outcome must carry*, in business language, **5–7 lines maximum**:

- ✅ "Support 1,000 concurrent users at peak"
- ✅ "p99 latency under 200ms on read endpoints"
- ✅ "99.99% uptime against the published SLO"
- ❌ "Use Wolverine with the standard outbox setup" (this is Context — already
  in CLAUDE.md / `docs/architecture.md`)
- ❌ "Wrap the handler with PolicyExecutor.Standard" (this is implementation)

When the constraints list grows past a handful, stop — something on it doesn't
belong. It's either Context (what the canon has already standardized) or a
failure condition (validator-checkable after).

**Constraints from CLAUDE.md** — auto-reference the rules that apply, pulled
from the *current* CLAUDE.md (read it; don't reconstruct from memory). For
each constraint name the rule + link to the section. Common candidates:

- **Server-controlled fields** — which fields come from the JWT `sub` claim or
  an authoritative source, NOT the request body
  → CLAUDE.md "Server-controlled fields are computed server-side"
- **IDOR predicate** — scoped read/write predicate in SQL, return 404 on
  non-owner (not 403)
  → CLAUDE.md "Security Requirements → Authorization"
- **Optimistic concurrency** — token on touched aggregates
  → CLAUDE.md "Performance Rules → Optimistic concurrency"
- **Outbox atomicity** — if events are published from non-handler code, the
  `BeginTransactionAsync → SaveChangesAsync → CommitAsync` wrap is required
  → CLAUDE.md "Observability & Context Propagation" + the linked deep dive
- **Required tests** — IDOR-integration test for new scoped endpoints,
  outbox-staging test for non-handler publishers
  → CLAUDE.md "Testing"
- **Async on request paths** — await everywhere, propagate `CancellationToken`
  → CLAUDE.md "Performance Rules → Async on request paths"
- **Aggregate IDs** — `Guid.CreateVersion7()` in factory methods
  → CLAUDE.md "Performance Rules → Entity IDs"
- **Long-running work / 202 Accepted** — if the synchronous path could exceed
  ~1s, reshape onto the message bus
  → CLAUDE.md "Performance Rules → Long-running work belongs on the message bus"
- **Mass assignment** — if a `[FromBody]` DTO has server-controlled fields,
  strip or re-validate against the authoritative source
  → CLAUDE.md "Security Requirements → Server-controlled fields"

Include only the constraints that apply to *this* feature; don't list every
rule.

> **This constraint list is a paraphrase of CLAUDE.md rules — keep it in sync
> when CLAUDE.md changes.** The PostToolUse hook surfaces this file in its
> worklist when CLAUDE.md is edited. `/check-rules` audits the alignment.
> See CLAUDE.md.

### 3. Significance check — ADR or not?

Ask whether this introduces or changes an architectural decision worth recording:

- New service / new bounded context → **yes**, draft an ADR
- New transport (gRPC peer, new ASB topic, third-party integration) → **yes**
- New auth model / tenancy model / security posture → **yes**
- New cross-cutting concern (caching, observability, security) → **yes**
- New CRUD endpoint following existing patterns → **no**
- Bug fix → **no**
- New saga participant following existing pattern → **only if the saga shape itself is new**

If yes, draft a sibling `docs/decisions/YYYY-MM-DD-<slug>.md` (ADR-style):

- **Context** — what problem this solves
- **Decision** — what we chose
- **Alternatives considered** — what we rejected and why
- **Consequences** — what this costs / unlocks

If `docs/decisions/` doesn't exist yet, mention that it would be created as
part of this feature's PR.

### 4. Outputs

Produce:

1. **GitHub issue body** — formatted ready to paste into `gh issue create`.
   Use the project's [work-item Issue Form fields](../../.github/ISSUE_TEMPLATE/work-item.yml)
   if possible (What / Why / Acceptance / Notes). Suggest labels:
   `type/feature`, the relevant `area/*`, `priority/*` if known.
   **Before creating: search for an existing issue** —
   `gh issue list --state open --search "<keywords>"` — and extend a live one
   rather than opening a near-duplicate, especially for epics (see CLAUDE.md
   "Search before you open an issue").

2. **Optional ADR draft** — if the significance check returned yes.

3. **Scaffolding suggestions** — concrete commands the user can run after
   spec approval to generate the code skeleton:
   - `/new-feature-slice <Service> <FeatureName>` per feature file needed
   - Note any DI registration that needs adding to `AddXInfrastructure`
   - Note any new aggregate that needs migration + factory method

### 5. Hole-test — close the gaps before shipping

Before the user opens the GitHub issue or runs `/new-feature-slice`, run one
final completeness check:

> *"Imagine handing this spec to someone who was not in your head — or to the
> AI implementing it next session. Where would they have to guess?"*

Walk through each section and mark every place an implementer (human or AI)
would infer rather than read. Common holes:

- Acceptance criteria that don't name failure modes (just success paths)
- Affects that name "OrderService" without naming which endpoints or events
- Constraints that say "follow IDOR pattern" without naming the predicate site
- Upstream dependencies that mention "Postgres version" without specifying which behavior the spec assumes
- Connections to other features that say "this touches the cart" without naming the cart endpoint contract

Every guess-point is a hole the agent will fill with its own assumption.
Close them in the spec before implementation starts. Per Kapil Viren Ahuja's
ICE framing: *"Every place they point is a hole you were about to let fill."*

### 6. Closing the loop

End with this prompt to the user (verbatim, so the loop-close is consistent):

> **This spec captures the handoff.** Once you've shipped, what did building
> this surface? Any "we should never write this again" or "we should always do
> this when" — encode it across the 5 surfaces. The spec is ephemeral; the
> lessons are how the loop compounds.

That prompt is what makes spec → implementation → encoding a closed loop.
Without it, the spec is just a TODO; with it, every feature feeds the canon.

## Style notes

- The spec is a handoff document, not a thesis. ~1 page of markdown is the target.
- Don't restate CLAUDE.md rules — *link* to them. The spec says "must satisfy
  X" and links to X.
- Don't draft the implementation — the spec is "what + must-be-true," not "how."
- If the feature description matches an existing pattern, name the canonical
  reference file (e.g. `OrderService/Features/PlaceOrder.cs` for the
  validate-persist-publish shape) and short-circuit.

## What this command is NOT

- **Not Spec Kit.** Doesn't produce a plan or task list. Just the structured spec.
- **Not a tutorial.** Doesn't re-explain CLAUDE.md rules — just references them.
- **Not implementation planning.** That's `/new-feature-slice` + writing code.
- **Not an auto-encoder.** It proposes the spec; the user decides whether to
  open the issue / draft the ADR / run the scaffolding.

## Example invocations

- `/feature-spec "Buyers can cancel a placed order within 5 minutes if payment hasn't completed"` — fully specified, jumps straight to clarifying questions
- `/feature-spec pasted` — uses the user's prior message as the feature description
- `/feature-spec` — asks the user what to spec

Run the spec on the feature identified by `$ARGUMENTS` now.
