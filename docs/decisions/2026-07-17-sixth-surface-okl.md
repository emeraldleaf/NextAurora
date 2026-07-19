# Two readers, one canon — the 6th surface, and how it became the OKL

- **Status:** Accepted (2026-07-17). Supersedes the "potential 6th surface (not built yet)" note in
  `CONTEXT.md` and the pointer in `.claude/commands/grid-infographic.md`.
- **One-line:** the tracked-but-unbuilt 6th surface is now built — not as in-repo microagents, but as a
  standalone cross-repo knowledge layer, the **OKL** (`okl` package / `emeraldleaf/okl`).

## Context

`CONTEXT.md` names five surfaces where encoded rules live (canon, PR rules, architecture review,
procedures, deep context) and flags a **potential 6th surface (not built): trigger-loaded microagents in
the OpenHands style** — with a pointer to *this file*, which until now did not exist. That dangling
pointer was itself an instance of the doc-orphan drift the method polices: a reference the canon makes to
a document nobody wrote. This ADR fills it and records what the surface actually became.

The original framing was **"two readers, one canon"**: a human reads the canon, and an agent reads the
canon, and the two must not drift apart. A 6th surface of trigger-loaded microagents was one candidate
mechanism to keep the agent-reader current. Building it surfaced a bigger problem the five surfaces don't
solve: **the canon is per-repo.** NextAurora/NovaCraft's canon, san-juan's canon, and Quartzose's canon
each re-learn the same lessons independently. "Two readers, one canon" is really "N readers, N canons."

## Decision

Build the 6th surface as an **Org Knowledge Layer (OKL)** that lives *outside* any single repo, so the
same encoded body is readable by every repo's agents:

- A typed, append-only store of **Defect / Gate / Rule / Retraction / Tombstone / Decision / PriorArt /
  Vocabulary / Claim** nodes with **CATCHES / ENCODES / REFUTES / RETRACTS / SUPERSEDES / VERIFIED_ON /
  RECURS_IN / CONTRADICTS / DEFINED_IN** edges.
- A **fail-closed** pre-task read (`okl check`, over MCP / a PreToolUse hook) — the trigger-loaded part of
  the original idea, but as an enforced gate rather than optional microagent context. A missing check
  blocks the edit (exit 2), the same way the mechanical gates block a merge.
- A **curation boundary**: `org`-scoped nodes propagate to every repo; `repo:<name>`-scoped nodes stay
  local. The scope decision is the governance step — only world-facts (prior art, API contracts,
  data-source gotchas, cross-cutting security patterns) go `org`.
- The five in-repo surfaces are unchanged. The OKL is where the *cross-repo* subset of the encoded body
  lives; each repo still keeps its stack-specific canon in `CLAUDE.md` + `.claude/`.

NovaCraft's canon is seeded into the OKL (`seed/novacraft-defects.json`): IDOR→404, JWT ClockSkew,
server-controlled fields, Wolverine outbox atomicity, handler-discovery≠DI, the messaging-topology race,
the dead-metric and speculative-interface lessons, and the tombstone gate. The React canon
(`frontend/CLAUDE.md`) is seeded org-scoped so any repo with a React UI inherits it.

## Why not in-repo microagents (the original sketch)

- Microagents living in `.claude/` would still be **per-repo** — they'd re-create the boundary this
  surface exists to erase.
- "Trigger-loaded context an agent *may* read" is surface-4 procedure, which the method already has. The
  gap was never "more optional context"; it was **enforcement + cross-repo reach**. A fail-closed check
  over a shared store delivers both; microagents deliver neither.

## What we learned building it (and folded back in)

- **It works where the knowledge exists, and coverage is the binding constraint.** A held-fixed A/B
  (same generator model, briefing injected vs not, blind judge≠generator) measured defect-reproduction
  dropping from **50% → 6%** on the tasks the store covered, and **75% → 8%** on the tasks where the
  baseline model actually failed. The two misses were both *coverage gaps* (a lesson not in the store),
  not method failures — so `okl check` doubles as a coverage detector.
- **The React gap is closed and re-measured.** The A/B caught the useEffect-fetching defect reproducing
  in *both* arms because no React node existed; after seeding NovaCraft's React canon org-scoped, the
  same task re-measured at **0% reproduction**.
- **A concurrent independent paper reached the same architecture** (Codified Context, arXiv 2602.20478 —
  tiered hot/cold memory + domain-expert agents + MCP retrieval on a 108K-line C# system) but is
  single-repo, agent-discretionary, and measures infrastructure growth, not recurrence. OKL's distinct
  wedge is exactly the four things that paper leaves open: cross-repo scope, fail-closed enforcement, a
  governance boundary, and a recurrence-after-arming metric.
- **A source-vs-spec drift detector** was added (`okl drift`) after that paper named spec-staleness as its
  primary failure mode: a node declares the files it governs, and drift fires when git shows those files
  changed after the node was last verified.

## Consequences

- `CONTEXT.md`'s 6th-surface note should change from "not built" to "built as the OKL — see this ADR."
- The OKL is a `SUPERSEDES`-style decision record; if the surface is ever restructured, supersede this
  file, don't edit it.
- Enforcement now spans two loci: in-repo gates (merge-time) and the OKL check (task-time, cross-repo).
