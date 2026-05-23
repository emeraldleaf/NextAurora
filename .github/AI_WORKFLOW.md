# AI Workflow

This document is the honest companion to the README's "How it was built" callout. The
README answers *what* I built with AI assistance; this answers *how*, *with what
guardrails*, and *what's verified versus drafted*. It's deliberately specific — vague
"used AI" claims are noise; the value is in the workflow itself.

## Tools in the loop

| Tool | Where it fits |
|---|---|
| **Claude Code (CLI + IDE)** | Primary drafting assistant. CLAUDE.md is loaded into every conversation. |
| **CodeRabbit** | Second-pass PR review. Configured in `.coderabbit.yaml` to apply this project's conventions. |
| **CodeQL** | Static security analysis on PRs + weekly. Configured in `.github/workflows/codeql.yml`. |
| **Dependabot** | NuGet (weekly, grouped) + Actions (monthly). Configured in `.github/dependabot.yml`. |
| **Roslyn analyzers** | Compile-time: Meziantou, SonarAnalyzer, Roslynator, BannedApiAnalyzers (with `BannedSymbols.txt`). |

## Guardrails I built into the assistant itself

The point of these isn't "stop the AI from being wrong" — the build, tests, CodeRabbit,
and analyzers do that. The point is to *shape* AI behavior toward this project's
conventions without me having to re-explain them in every prompt.

### Hooks (`.claude/settings.json`)

- **`SessionStart` → `inject-status.sh`**. Surfaces the top of `docs/STATUS.md` at the
  start of every session. Stops the assistant from cold-reading the repo and lets it
  pick up where the last session left off. CLAUDE.md names STATUS.md as the cross-session
  entry point — this hook enforces the rule mechanically.

- **`PreToolUse` (Edit | Write) → `block-sync-over-async.sh`**. Inspects proposed edits
  to `.cs` files; rejects ones containing `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`
  with a citation back to CLAUDE.md "Performance Rules". The build also rejects these via
  `BannedSymbols.txt`, but the hook catches them at *propose* time — the bad diff never
  lands. Faster feedback loop.

- **`PostToolUse` (Edit | Write) → `check-claude-md-refs.sh`**. When CLAUDE.md itself is
  edited, lists every file that contains the `See CLAUDE.md` cross-reference marker so
  paraphrases can be reviewed for drift. Convention is documented in CLAUDE.md "Debugging
  Discipline".

### Slash commands (`.claude/commands/`)

Project-specific commands that bake repeated motions into a single keystroke and a
consistent convention.

- **`/new-feature-slice <ServiceName> <FeatureName>`** — scaffolds a VSA feature file
  (command/query record + validator + handler) matching the canonical shape from
  `OrderService/Features/PlaceOrder.cs`. Refuses for CatalogService because that service
  uses Clean Architecture on purpose.

- **`/sync-status`** — refreshes `docs/STATUS.md` from `git log` + open issues, diff-style.
  Always asks for confirmation before applying.

- **`/check-rules`** — audits every `See CLAUDE.md` paraphrase against the canonical rule
  text. Reports drift, doesn't auto-fix.

### Agent (`.claude/agents/architecture-reviewer.md`)

A dedicated subagent that reviews a target file or diff against CLAUDE.md's SOLID / DDD /
VSA-vs-Clean / Performance rules. Doesn't write code — produces a categorized findings
report ("must fix" / "should consider" / "aligned"). Use before merging non-trivial
architectural changes.

### Skills (`.claude/skills/`)

Project-owned skills (built for this repo):

- **`dotnet-performance`** — EF Core + .NET 10 performance guidance loaded on demand when
  writing handlers, queries, repositories, or middleware. Surfaces the *why* behind every
  CLAUDE.md "Performance Rules" entry.
- **`excalidraw-diagram`** — for generating architecture diagrams.

Community skills (curated, security-audited before install):

- **`skill-security-auditor`** ([alirezarezvani](https://github.com/alirezarezvani/claude-skills)) —
  pre-install security gate. Scans skill directories or git repos for code-execution risks,
  prompt injection, supply-chain issues, and filesystem-boundary violations. Run this on
  every community skill *before* installing the rest. The audit is the gate; PASS → install,
  CRITICAL/HIGH → don't.
- **`verification-before-completion`** ([obra/superpowers](https://github.com/obra/superpowers)) —
  reinforces the README "How it was built" runtime-verification claim and the PR template's
  verification checklist. Won't let me claim "fixed" without actual command output.
- **`systematic-debugging`** ([obra/superpowers](https://github.com/obra/superpowers)) —
  four-phase root-cause analysis. Pairs with CLAUDE.md "Debugging Discipline" so the
  *how* (the skill) and the *capture rule* (CLAUDE.md) work together.
- **`variant-analysis`** ([trailofbits/skills](https://github.com/trailofbits/skills)) —
  highest-leverage skill for a microservices repo: find one anti-pattern, search for the
  same shape across all 5 services. Ships CodeQL + Semgrep query templates by language.
- **`test-driven-development`** ([obra/superpowers](https://github.com/obra/superpowers)) —
  RED-GREEN-REFACTOR discipline + a curated list of testing anti-patterns.
- **`using-git-worktrees`** ([obra/superpowers](https://github.com/obra/superpowers)) —
  isolated parallel-development workflow. Pairs with `dispatching-parallel-agents` patterns
  (which I didn't install separately because Claude Code supports them natively).
- **`writing-plans`** + **`executing-plans`** ([obra/superpowers](https://github.com/obra/superpowers)) —
  paired skills for spec-driven implementation with human review checkpoints. Used for
  non-trivial multi-step features.

**Skills I deliberately did NOT install** (curation > breadth):

| Skill | Why skipped |
|---|---|
| webapp-testing, frontend-design, shadcn/ui, web-artifacts-builder | Storefront/SellerPortal are static scaffolds — no frontend work |
| mcp-builder | Not building an MCP server |
| ci-cd-pipeline-builder | Existing CI works; adding another generator is anti-pragmatic |
| github-ops | `gh` CLI is fine |
| static-analysis (CodeQL+Semgrep+SARIF), differential-review | Direct overlap with existing CodeQL + SonarAnalyzer + CodeRabbit + the architecture-reviewer agent |
| subagent-driven-development, dispatching-parallel-agents | Claude Code supports parallel agents natively |
| get-shit-done | Workflow methodology; YMMV |
| skill-creator | Install only when building a custom skill |

The signal is the *curation*, not the count. Eight community skills that earn their keep
beats twenty that came with the bundle.

### Architecture map (`.claude/architecture-map.md`)

A structured, AI-consumable map of the repo: services, their shapes (Clean vs VSA),
which databases they own, who publishes which events, where each port is registered.
Read by the architecture-reviewer agent to orient itself; useful for humans too.

## What's AI-assisted vs hand-written

Pragmatically: most code in this repo was drafted with AI assistance and then verified by
me. Every PR uses the `PULL_REQUEST_TEMPLATE.md` to make that distinction explicit per
change. The categories there are:

1. **Pure AI-assisted, human-verified** — most common.
2. **AI-assisted with manual edits** — common for non-trivial refactors.
3. **Hand-written** — usually small fixes or doc tweaks.
4. **AI-generated, not yet verified** — marked as draft / WIP.

If you're a reviewer and a change feels too clean to be real, check the verification
section of the PR. If the boxes aren't checked, that's a flag to push back.

## What I don't use AI for

- **Final security decisions.** CodeQL + manual review + the security-review skill cover
  this, and authentication/authorization changes always get a manual pass.
- **Production deploy choices.** The Fly.io deployment, AWS migration plan, and CI/CD
  pipelines are designed by me. AI helps with the YAML but doesn't decide architecture.
- **The README and CLAUDE.md.** Those are my voice. AI drafts get rewritten before
  landing.

## How this stays honest

- **Verification claims are concrete.** "Manually exercised the changed code path" with
  the actual curl / command run, not "looks good locally".
- **Open issues stay in STATUS.md** — explicitly, not hidden. The README "About this
  repo" callout admits up front that this is a monorepo with two architectures and not
  every code path has been runtime-verified.
- **`See CLAUDE.md` paraphrases get audited** via `/check-rules` so the canonical and
  the inline don't drift.
