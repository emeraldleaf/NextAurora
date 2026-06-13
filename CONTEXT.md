# NextAurora — Domain Vocabulary

A focused glossary for AI sessions and human contributors. **Not** a rules
file — for rules, see `CLAUDE.md`. This file captures the *vocabulary* the
project uses, so language is consistent between humans, agentic coding tools
(Claude Code, Copilot, etc.), and review surfaces (CodeRabbit, agents).

Pattern stolen from [Matt Pocock's skills repo](https://github.com/mattpocock/skills),
adapted for our encoding-loop method.

---

## Language

### Encoding loop

The method this project uses to keep agent-assisted code from drifting. A
feedback loop where each finding, plan, fix, or audit becomes a rule that
gets encoded at the smallest sufficient surface and promoted down the
enforcement spectrum as it earns its keep.

*Avoid:* "spec-driven workflow" (that's a harness, not a method),
"feedback automation" (too generic).

### Encoding response

What you actually do when the loop fires, regardless of what triggered it.
Always the same two-step pattern: **Pick the smallest durable surface →
Encode the rule → Promote down the spectrum**.

### Trigger

The external event that surfaces a candidate rule. Always *outside* the
loop. Examples: planning a feature, finding a bug, fixing a regression,
auditing for a pattern. The *trigger varies; the response is the same.*

*Avoid:* using "trigger" to describe an internal loop step.

### 5 surfaces

The five places encoded rules live in this project:

1. **Canon** — `CLAUDE.md` + `.claude/` (always-on)
2. **PR rules** — `.coderabbit.yaml` (path-scoped)
3. **Architecture review** — pattern checklist in the `architecture-reviewer` agent
4. **Procedures** — skills + slash commands in `.claude/`
5. **Deep context** — `docs/` + paired diagrams

A potential **6th surface** (not in NextAurora yet, but tracked): trigger-loaded
microagents in the OpenHands style — see `docs/post-ideas/two-readers-one-canon.md`.

*Avoid:* "the canon" when you mean the broad body of all encoded rules across
all surfaces — that's ambiguous (canon is also the name of surface #1). Use
**"the encoded body"** for the whole thing; use **"canon"** only for surface #1.

### Canon (the surface)

Specifically surface #1: `CLAUDE.md` plus the contents of `.claude/`. The
always-loaded layer. Subject to the **size budget** (soft 400 / hard 500
lines for `CLAUDE.md` itself).

### 3 tiers

The enforcement spectrum a rule can live at:

- **Tier 1 — Convention** (held by humans + AI on review)
- **Tier 2 — PR-review automation** (CodeRabbit, architecture-reviewer agent, PostToolUse hooks)
- **Tier 3 — Mechanical gates** (build, analyzers, arch-tests, CI scripts, pre-commit hooks)

*Promote down* the spectrum as a rule earns its keep — start at Tier 1, move
to Tier 2 when humans miss it consistently, move to Tier 3 when it's worth
the build-time enforcement cost.

### Promote down spectrum

The act of moving a rule from a softer tier to a harder tier. Tier 1 → Tier 2
when humans miss the rule. Tier 2 → Tier 3 when the rule is worth a
build-failure-class enforcement.

*Avoid:* "automate", "mechanize" (too imprecise — promotion is specifically
down the *spectrum*, not generic automation).

### Smallest durable surface

The minimum surface that holds a rule effectively. Pick this on encode, not
the *strongest* surface. A rule that only matters in `Get*.cs` files should
live in a path-scoped `.coderabbit.yaml` instruction, not in always-loaded
CLAUDE.md.

### Mechanical floor

The four loop disciplines that ARE enforced by mechanical gates today:

1. **Greppable paraphrases** — paraphrases of CLAUDE.md rules end with `See CLAUDE.md.` so the cross-reference audit can find them
2. **File-rename detection** — PostToolUse hooks flag stale refs after `git mv`/`git rm`
3. **Doc + diagram pairing** — every `docs/*.excalidraw` has a sibling `.svg`; CI fails if not
4. **Lean canon** — `CLAUDE.md` size budget (soft 400 / hard 500)

### Convention only

The two loop disciplines that **no mechanical gate** can catch — you hold them:

5. **Presence in the loop** — approval at the gate isn't presence; staying engaged during implementation is
6. **Budgeted experiments** — every experiment gets a token budget + stop-time; default at exhaustion is *stop*

### Loop disciplines

The combined set of 4 mechanical floor + 2 convention only = 6 disciplines
that make the encoding loop function.

*Avoid:* "Cross-cutting considerations" (template-inherited from a different
infographic style and doesn't fit this diagram).

### Cross-reference paraphrases

The audit step where you grep for all the places a CLAUDE.md rule has been
paraphrased (in inline comments, in skill files, in the architecture-reviewer
agent, etc.) and update each so they stay aligned. Runs every time CLAUDE.md
changes. Surfaces via `/check-rules`.

### Paired docs

When a rule has detail that exceeds CLAUDE.md's one-paragraph budget, the
*headline + one-paragraph summary* stays in CLAUDE.md and the detail moves
to a paired theme-doc in `docs/`. The paraphrase ends with `See CLAUDE.md.`
so it's greppable.

### Value gate

The 3-question gate at the start of `/feature-spec`:

1. Who needs this, and what breaks for them if it never exists?
2. Would we still build it if it cost a week of engineering time?
3. Who owns saying no to this?

If question 1 surfaces *"no one,"* the work is an *experiment*, not a feature.

### Gap check

The completeness check in `/feature-spec` Step 5. Walk every section and ask:
*"Where would an implementer have to guess?"* Every guess-point is a hole
the agent will fill silently. Close them in the spec before implementation
starts.

*Avoid:* "Hole test" (was previously used; renamed for accessibility — most
LinkedIn readers don't recognize the IDSD framework reference).

### Harness vs method

The framing distinction this project orients around.

- **Harness** — a tool/framework that owns the workflow (Spec Kit, BMAD, GSD).
  Useful but expensive when the process itself becomes the bug-source.
- **Method** — the discipline of *encoding rules into review surfaces* so
  every iteration compounds quality and prevents drift. Survives swapping
  harnesses; outlives any specific tool.

*Avoid:* using "harness" to mean a coding agent like Claude Code or Copilot
— those are **agentic coding tools**. Harness is reserved for the
workflow-owning frameworks specifically.

### Drift

The state where encoded conventions decay — paraphrases of rules diverge,
canonical files reference deleted files, doc and diagram pairs drop out of
sync, the same bug returns across PRs. The thing the encoding loop is
designed to prevent.

### Compounding

The claim that each rule encoded once stays encoded forever, so quality
*accumulates* over time rather than resetting per-session. Currently
intuition, not measurement — see `docs/post-ideas/measuring-the-encoding-loop.md`.

### Feedback loop

The shape of the encoding loop's mechanism: a trigger fires, the response is
encoded, future triggers of the same class get caught earlier (Tier 2 then
Tier 3) instead of recurring.

### Agentic coding tools

Tools like Claude Code, GitHub Copilot (Agent Mode), OpenHands, Aider, Cursor,
Cline. **Distinct from harnesses** — these are the things being shaped by
the encoding loop; harnesses are the things competing with the encoding loop
as a workflow.

---

## Relationships

- **The encoding loop** has 1 response composed of 2 actions (**Encode** →
  **Promote**), 5 surfaces it can encode to, 3 tiers it can promote across,
  and 6 disciplines that hold it together.
- **A rule** lives in one or more **surfaces** at one or more **tiers**.
  Rules can be in multiple surfaces simultaneously (a rule about IDOR may
  live in CLAUDE.md, in .coderabbit.yaml, and in an integration test
  pattern). Each instance can be at a different **tier**.
- **A trigger** is outside the loop. It supplies input. The loop's **response**
  is invariant to which trigger fired.
- **A discipline** is *not* a rule — it's a meta-rule about how rules are
  managed. The 4 mechanical floor disciplines + 2 convention-only disciplines
  govern *how the system stays healthy*, not *what specific code should do*.

---

## Flagged ambiguities

- **"Canon"** — historically used to mean both (a) surface #1 specifically
  (CLAUDE.md + .claude/) and (b) the broad body of all encoded rules across
  all five surfaces. **Resolved:** *canon* = surface #1; *the encoded body*
  = all rules across all surfaces. Always disambiguate in docs.

- **"Spec"** — multiple meanings: (a) the Spec Kit per-feature contract
  (avoid this framing for our work — we don't use Spec Kit), (b) a feature
  spec in the `/feature-spec` skill sense (the *handoff document*, not the
  contract). **Resolved:** when we say "spec," we mean *the `/feature-spec`
  output*, not a Spec-Kit-style contract.

- **"Trigger"** — historically used both for *what fires the encoding loop*
  (the user's term, now canonical here) and for *Claude Code's PreToolUse /
  PostToolUse hook events* (Claude Code terminology). **Resolved:** when we
  say *trigger* in encoding-loop context, we mean a loop entry point.
  When discussing hooks, say *hook event*.

- **"Drift"** — currently used loosely to cover both (a) paraphrase drift
  between CLAUDE.md and its mirrors, and (b) general convention decay over
  time. **Both are valid usages**; context disambiguates. Not yet a problem.

- **"Method" vs "methodology"** — interchangeable in this project. We use
  *method* in the LinkedIn post because it's shorter and punchier;
  *methodology* in docs for precision. Either is fine.
