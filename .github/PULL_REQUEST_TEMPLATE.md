<!--
NextAurora PR template. Sections marked [required] must be filled in before merge.
Sections marked [skip if N/A] can be deleted if the change doesn't touch that area.
-->

## What changed [required]

<!-- 1-3 sentences. The "why", not just the "what". The diff shows what. -->

## How it was built [required]

<!--
Be specific. The point isn't to confess — the point is to make verification claims
honest. Hiring managers, future-you, and CodeRabbit all read this.

Pick the closest fit and edit it. Delete the others.
-->

- [ ] **Pure AI-assisted, human-verified.** Used Claude Code (or equivalent) to draft. I read every line, ran the build, and manually exercised the changed path before pushing.
- [ ] **AI-assisted with manual edits.** Started from an AI draft, then refactored / fixed / added detail by hand. Verification as above.
- [ ] **Hand-written.** No AI assistance on this change.
- [ ] **AI-generated, not yet verified.** Marking as draft / WIP. Will verify before requesting review.

If AI was involved: link to the conversation transcript or commit messages that describe
the AI workflow (e.g. `gh issue view N` if the conversation is preserved in an issue).

## Architecture impact [skip if N/A]

<!--
Anything from this list applies? Check it and explain. If none apply, delete this section.
-->

- [ ] Adds a new domain entity / aggregate / value object
- [ ] Adds a new event contract (in `NextAurora.Contracts/Events`)
- [ ] Adds a new repository or port interface
- [ ] Adds a new service or splits an existing one
- [ ] Changes a published API surface (REST or gRPC)
- [ ] Touches the outbox, sagas, or messaging topology
- [ ] Modifies CLAUDE.md or a `See CLAUDE.md` paraphrase

For non-trivial architectural changes, consider invoking the `architecture-reviewer` agent
locally before requesting review.

## Verification [required]

<!--
The CLAUDE.md "How it was built" section in README is precisely about this — verification
beats vibes. Be specific about what you actually exercised.
-->

- [ ] `dotnet build` clean (zero warnings — TreatWarningsAsErrors is on)
- [ ] `dotnet test` passes locally
- [ ] Manually exercised the changed code path:
  - <e.g. `curl /api/v1/orders` and observed expected response>
  - <e.g. published an `OrderPlacedEvent` and watched PaymentService consume it>
- [ ] If this touches integration tests: ran the Testcontainers slice and confirmed Docker socket is reachable
- [ ] If this touches AppHost: ran `dotnet run --project NextAurora.AppHost` and confirmed all services reach "Running" (not "Finished") in the dashboard

## Deferred / known gaps [skip if N/A]

<!-- Anything not done that future-you / reviewers should know about. -->

## Linked docs / issues [skip if N/A]

<!-- STATUS.md update? Issue closed? Decision recorded in performance-and-data-correctness.md? -->
