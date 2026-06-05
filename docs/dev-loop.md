# Development Loop — Tooling & Configuration

A pragmatic inventory of every tool, hook, agent, skill, and config that shapes
how code lands in this repo, organized by *when* in the dev loop each one
fires — plus the **reflexive rule-encoding step that closes the loop**.

Visual companion: [dev-loop.svg](dev-loop.svg) (source: [dev-loop.excalidraw](dev-loop.excalidraw)).

The bar this document is held to: **describe what's actually in place** (with
file paths so you can verify), **call out gaps honestly**, and **for each gap,
propose a pragmatic solution sized to the actual problem** — no
build-a-whole-new-system suggestions.

Last updated: 2026-05-29.

---

## At a glance

```
              ┌──── encode in CLAUDE.md + .claude/ + .coderabbit.yaml + docs ────┐
              ↓                                                                   │
  Edit ───→ Build ───→ Test ───→ PR ───→ Merge ───→ Runtime                       │
                                  │                                               │
                                  └────────────── review surface ─────────────────┘
                                                  (CodeRabbit / architecture-reviewer /
                                                   test failure / incident)
```

Five forward stages — *Edit → Build → Test → PR → Merge/Runtime* — closed by a
sixth, **reflexive** step: every non-obvious finding gets encoded across the
five surfaces described under "The reflexive step" so the next instance is
caught earlier, not re-derived. The reflexive step is what makes the loop
compound. Without it, the same findings recur every PR cycle.

---

## This is a method, not a harness

Claude Code, CodeRabbit, GitHub Actions, Excalidraw, the `dotnet-performance` skill — these are **harnesses** that carry out work. The encoding loop is the **method** that defines what the work is, where rules live, and how findings turn into durable encoded knowledge.

- Harness without method → agents fill gaps the human never thought through; the "spec was good but the model strayed" failure mode
- Method without harness → well-defined rules nobody runs
- Method paired with the right harnesses → the encoding loop

When comparing the loop to Spec Kit, BMAD, Kiro, Tessl, Agent OS, Kapil Viren Ahuja's Garura: **those are harnesses**. The encoding loop is the method. Adopting a harness without a method is the default failure mode today because the harness is the part with a download button.

The encoding loop's method consists of:
- **`CLAUDE.md` + the `.claude/` folder** (the canon + agents + skills + commands + hooks)
- **The 5 encoding surfaces** (where rules live — see next section)
- **The 3-tier enforcement spectrum** (how rules get caught — see [The enforcement spectrum](#the-enforcement-spectrum))
- **Continuous Rule Encoding** (how lessons compound — see [The reflexive step](#the-reflexive-step-continuous-rule-encoding))
- **`/feature-spec`** (the per-feature handoff that ties intent + canon together)

Claude Code, CodeRabbit, the GitHub Actions runner — those are the harnesses that carry it out.

---

## The reflexive step: Continuous Rule Encoding

Per [CLAUDE.md "Continuous Rule Encoding"](../CLAUDE.md#continuous-rule-encoding-the-compounding-loop),
when *any* review surface (PR-time CodeRabbit, architecture-reviewer agent
pass, integration-test failure, prod incident, security audit) surfaces a
pattern or antipattern worth keeping, it gets encoded the same session —
across as many of the **five surfaces** below as apply. Default to the
**smallest set** — and the *smallest entry on CLAUDE.md*, because every
byte there is loaded into every session.

### The five surfaces (canonical list)

| # | Surface | What goes here | Catches violations at... |
|---|---|---|---|
| 1 | [`CLAUDE.md`](../CLAUDE.md) | **Always-on** rules only. Prefer one-line headline + link to the deeper doc; full paragraphs are usually a sign the rule belongs in surface 5 with a pointer here. | Code-generation time (Claude reads CLAUDE.md every session) |
| 2 | [`.coderabbit.yaml`](../.coderabbit.yaml) `path_instructions` | File-pattern-scoped guidance — add a new glob if no existing one fits. **Most per-file rules belong here, not in CLAUDE.md.** | PR-time CodeRabbit review |
| 3 | [`.claude/agents/architecture-reviewer.md`](../.claude/agents/architecture-reviewer.md) Pattern Checklist | Per-file-category scan rule the agent applies on every review | Local agent invocation, before code lands |
| 4 | [`.claude/skills/`](../.claude/skills/) + [`.claude/commands/`](../.claude/commands/) | Procedural knowledge worth a dedicated bundle (multi-step, specialized vocab) | On-demand, or when the user describes the right intent |
| 5 | Supporting docs + paired diagrams ([architecture.md](architecture.md), [performance-and-data-correctness.md](performance-and-data-correctness.md), this file, and `docs/*.{svg,excalidraw}` pairs) | The *why* behind a rule, the visual depiction reviewers reason against | Onboarding, PR review, future-you |

**Deferral surface (NOT part of the loop):** [GitHub Issues](https://github.com/emeraldleaf/NextAurora/issues)
with the `rule-encoding-deferred` label tracks findings where code shipped
but the encoding hasn't yet. The issue is a placeholder — a TODO that the
encoding eventually happens. **It is not itself the encoding** — closed
issues aren't read by future sessions. The rule lives in surfaces 1–5; the
issue exists only until it does.

### What triggers an encoding (the threshold)

Same as the [Debugging Discipline](../CLAUDE.md#debugging-discipline) rule:
**if the next person could repeat the mistake (or re-derive the rule from
first principles), the rule belongs in writing.** Don't encode trivial style
nits; do encode security patterns, performance traps, concurrency hazards,
distributed-systems gotchas, anti-IDOR patterns, outbox traps — anything
cross-cutting.

A **merged fix PR without the corresponding `.claude/` encoding is a
half-finished job.** The fix lives in the PR but the rule lives in `.claude/`.
Both should land together (single PR with both, or paired PRs when separation
is cleaner).

### The cross-reference convention (`See CLAUDE.md.`)

CLAUDE.md is canonical; everywhere else summarizes. Convention: any inline
comment in `.cs` / `.props` / `.csproj` / `.md` that paraphrases a CLAUDE.md
rule ends with the literal token `See CLAUDE.md.` — that makes the worklist of
paraphrases greppable.

Two mechanical enforcers:

- [`.claude/scripts/check-claude-md-refs.sh`](../.claude/scripts/check-claude-md-refs.sh)
  — `PostToolUse` hook. When CLAUDE.md changes, lists every file containing
  `See CLAUDE.md` so drift can be reviewed in the same session.
- [`/check-rules`](../.claude/commands/check-rules.md) — slash command. Audits
  every paraphrase against the canonical rule and flags drift.

### How the architecture-reviewer agent closes the loop

The agent **prompts for encodings**. Step 7 of its workflow surfaces a "Rules
to encode" section in its output, asking concretely *which* CLAUDE.md section
/ `.coderabbit.yaml` glob / Pattern Checklist category a finding belongs to.
So the encoding doesn't get dropped between "found in review" and "fixed in
PR."

For invocation patterns — when to use an agent vs a slash command vs a skill,
and how each is triggered — see [`.claude/README.md`](../.claude/README.md).
The decision tree there is the single source of truth.

---

## The enforcement spectrum

For any architectural rule, there's a sliding scale of how strictly it's
enforced:

```
convention ───→ architecture tests ───→ project split
   (cheap)        (deterministic)       (compiler-enforced)
```

| Mechanism | Cost | What it catches |
|---|---|---|
| **Convention** (CLAUDE.md + `.coderabbit.yaml` + agent review) | ~0 | Most things, most of the time. Relies on author + reviewer + agent. |
| **Architecture tests** ([NetArchTest](https://github.com/BenMorris/NetArchTest)) | One test-suite slot | Deterministic at `dotnet test`. [tests/NextAurora.ArchitectureTests/DependencyRuleTests.cs](../tests/NextAurora.ArchitectureTests/DependencyRuleTests.cs) asserts every service's `Domain/` namespace has no dependency on EF Core, ASP.NET, Wolverine, Npgsql, SqlClient, Dapper, or caching — the **dependency rule of Clean Architecture, enforced in a single-project VSA shape** without the four-project ceremony. |
| **Project split** (Clean Architecture's 4-project layout) | High setup + ongoing weight | Compiler-enforced. Earned by complexity — not the default. |

### Promotion Signal — when to consider Clean Architecture

VSA is the default and stays the default. From CLAUDE.md "Promotion signal":

| Signal | Shape |
|---|---|
| ≤4 aggregates per service, ≤10 features, single team | VSA (current default) |
| 5+ aggregates with cross-cutting domain rules that several features coordinate on, AND `Domain/` growing faster than `Features/` | Consider Clean Architecture promotion |
| "I want to mock the DbContext in unit tests" | **NOT** a reason. Use integration tests with Testcontainers (see Stage 3). |

None of the services are at that scale today. The previous Clean Architecture
attempt in CatalogService was retired in the VSA collapse refactor precisely
because none of those signals were hit. The architecture-tests rung gives VSA
the dependency-rule guarantee that Clean's project split would give — at a
fraction of the structural cost.

---

## Stage 1 — Edit-time (IDE + Claude Code)

This is where most code originates. The tooling here shapes proposed edits
*before* they land in a file.

### Canonical rules
| File | Role |
|---|---|
| [CLAUDE.md](../CLAUDE.md) | The canonical opinionated rule set — SOLID/DDD/VSA-vs-Clean, performance, security, communication patterns, debugging discipline, the Continuous Rule Encoding loop itself. Loaded into every Claude Code session. |
| [.editorconfig](../.editorconfig) | Naming + formatting enforced by Roslyn at build time. |
| [Directory.Build.props](../Directory.Build.props) | Shared build settings (TreatWarningsAsErrors, target framework, analyzers). |
| [Directory.Packages.props](../Directory.Packages.props) | Central package management — versions live here, csproj files have no version attributes. |
| [BannedSymbols.txt](../BannedSymbols.txt) | Banned APIs (`Task.WaitAll`, `Parallel.For`, `Thread.Sleep`, etc.) enforced by `BannedApiAnalyzers`. |

### Hooks ([.claude/scripts/](../.claude/scripts/))
| Hook | Event | What it does |
|---|---|---|
| [block-sync-over-async.sh](../.claude/scripts/block-sync-over-async.sh) | `PreToolUse` (Edit\|Write on .cs) | Rejects `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` *in proposed edits*. Build-time net (BannedSymbols.txt) catches the same patterns later — this hook catches them earlier so the bad diff never lands. |
| [inject-status.sh](../.claude/scripts/inject-status.sh) | `SessionStart` | Injects top of STATUS.md + current branch + last commit so sessions don't start cold. |
| [check-claude-md-refs.sh](../.claude/scripts/check-claude-md-refs.sh) | `PostToolUse` (Edit\|Write on CLAUDE.md) | Implements the `See CLAUDE.md.` cross-reference convention. When CLAUDE.md changes, lists every paraphrase site so drift can be reviewed in the same session. |

### Slash commands ([.claude/commands/](../.claude/commands/))
| Command | Purpose |
|---|---|
| [/new-feature-slice](../.claude/commands/new-feature-slice.md) | Scaffolds a VSA feature slice matching the [OrderService/Features/PlaceOrder.cs](../OrderService/Features/PlaceOrder.cs) canonical shape. |
| [/feature-spec](../.claude/commands/feature-spec.md) | Drafts a structured feature spec (goal + acceptance + auto-referenced CLAUDE.md constraints) — the handoff between intent and implementation. Feeds the encoding loop. |
| [/article-audit](../.claude/commands/article-audit.md) | Audits an external article (community blog, post, conference talk) against CLAUDE.md and the encoding surfaces — outputs a coverage map + verdict + draft issue body for any gaps. |
| [/sync-status](../.claude/commands/sync-status.md) | Refreshes STATUS.md from `git log` + open issues. |
| [/check-rules](../.claude/commands/check-rules.md) | Audits every `See CLAUDE.md` paraphrase against the canonical rule and flags drift. |

### Agents ([.claude/agents/](../.claude/agents/))
| Agent | Purpose |
|---|---|
| [architecture-reviewer](../.claude/agents/architecture-reviewer.md) | Loads CLAUDE.md + [architecture-map.md](../.claude/architecture-map.md), evaluates a target against SOLID/DDD/VSA-vs-Clean/Performance/Security rules using a per-file-category **Pattern Checklist**. Reports only — no edits. Step 7 of its workflow prompts for encodings (see "The reflexive step" above). |

### Skills ([.claude/skills/](../.claude/skills/))

Ten skills are installed. Honest framing: they fall into three usage tiers,
not one. The dev-loop is **load-bearing on three of them**; the rest are
either ambient (discipline absorbed into CLAUDE.md rules and behavior without
formal invocation) or dormant (fire only on specific triggers that don't
happen often). Listed accurately so the table doesn't overclaim.

**Tier 1 — Actively used (named invocations in the work itself)**

| Skill | Source | What "actively used" means here |
|---|---|---|
| **dotnet-performance** | **this repo (project-authored)** | Load-bearing as the *deeper-guidance target* for CLAUDE.md Performance Rules — the preamble points at it explicitly, [architecture-reviewer.md](../.claude/agents/architecture-reviewer.md) routes profiling work to it, [.github/AI_WORKFLOW.md](../.github/AI_WORKFLOW.md) names it as the canonical reference. Used as a reference surface, not an auto-fired procedure. |
| excalidraw-diagram | this repo | Fires when diagrams change. Three lesson-encoding commits (text-overlap + GitHub-SVG fixes); [`.claude/scripts/rebuild-diagrams.sh`](../.claude/scripts/rebuild-diagrams.sh) regenerates [dev-loop.svg](dev-loop.svg) from it. |
| writing-plans + executing-plans | [obra/superpowers](https://github.com/obra/superpowers) | Fires on explicit multi-step planning work. Canonical use: the Hetzner + Dokploy deployment plan rewrites in [docs/full-saga-deployment-plan.md](full-saga-deployment-plan.md). |

**Tier 2 — Ambient (disciplines absorbed; skill rarely invoked by name)**

The principles these skills encode are present in CLAUDE.md rules and shape
behavior on every PR, but the skills themselves are not formally loaded as
named procedures during routine work. They earn their keep as *available
fallbacks* when the discipline needs to be re-established or taught.

| Skill | Source | How it actually shows up |
|---|---|---|
| verification-before-completion | [obra/superpowers](https://github.com/obra/superpowers) | Discipline absorbed into CLAUDE.md "Testing" (`dotnet build` clean, analyzer warnings as errors) + PR template's Verification section. Skill itself is reach-for-when-needed. |
| systematic-debugging | [obra/superpowers](https://github.com/obra/superpowers) | `.claude/README.md` calls it "auto-triggers," but in practice routine bug-hunting happens inline; the skill formally fires when the bug resists the inline approach. |
| test-driven-development | [obra/superpowers](https://github.com/obra/superpowers) | Discipline absorbed into CLAUDE.md "Testing" required-test patterns (IDOR test, outbox-non-handler test, AAA narrative comments). The RED→GREEN→REFACTOR cadence is the reach-for shape when greenfield logic genuinely needs it. |
| variant-analysis | [trailofbits/skills](https://github.com/trailofbits/skills) | One-bug-found-now-search-siblings happens ambiently via grep; the formal skill fires when the search needs to be more rigorous than a one-shot pattern match. |

**Tier 3 — Installed but dormant (correctly — fire only on specific triggers)**

| Skill | Source | The trigger that hasn't happened (yet) |
|---|---|---|
| skill-security-auditor | [alirezarezvani/claude-skills](https://github.com/alirezarezvani/claude-skills) | Pre-install gate for new community skills. No new skill installs recently → dormant by design, not neglect. |
| using-git-worktrees | [obra/superpowers](https://github.com/obra/superpowers) | Workspace isolation for parallel branches. The project's flow uses ordinary feature branches; worktrees haven't been needed. |

### Architecture map
[.claude/architecture-map.md](../.claude/architecture-map.md) — code-graph for
AI + humans. Services, shapes, event flow, ports, aggregates, concurrency
tokens.

### Secondary AI reviewer
[GitHub Copilot](https://github.com/features/copilot) (GPT-5) in-editor for
second-opinion diff review. Conventions encoded in
[.github/copilot-instructions.md](../.github/copilot-instructions.md). The
principle: disagreement between Claude and Copilot is a signal to dig deeper,
not pick the louder voice.

---

## Stage 2 — Build-time (`dotnet build`)

Static analysis that runs as part of every build. `TreatWarningsAsErrors` is
on; zero warnings allowed.

| Analyzer | Catches |
|---|---|
| **Meziantou.Analyzer** | C# best practices — design, performance, security, usage. ~200 rules. |
| **SonarAnalyzer.CSharp** | Code smells, bugs, vulnerabilities — same engine as SonarQube/SonarCloud. |
| **Roslynator.Analyzers** | Refactoring + style suggestions. |
| **BannedApiAnalyzers** + [BannedSymbols.txt](../BannedSymbols.txt) | Forbidden concurrency hazards (Task.WaitAll, Parallel.For, Thread.Sleep, etc.) with custom replacement guidance. |
| **C# nullability** | NRTs enabled — null-state analysis catches most NREs at compile. |
| Standard .NET 10 compiler warnings | Treated as errors. |

---

## Stage 3 — Test-time (`dotnet test`)

### Tooling

| Tool | Purpose |
|---|---|
| **xunit** | Test runner. |
| **AwesomeAssertions** | Fluent assertion library (drop-in fork of FluentAssertions 8). |
| **NSubstitute** | Mocking for unit tests. |
| **Microsoft.AspNetCore.Mvc.Testing** + `WebApplicationFactory` | In-process API hosting for integration tests. |
| **Testcontainers** | Real DB / Redis / messaging via Docker for integration tests. macOS uses `~/.docker/run/docker.sock`; CI uses standard path. |
| **NetArchTest** | Architecture-tests rung — see "The enforcement spectrum" above. |
| **Wolverine.Tracking** (`TrackActivity().ExecuteAndWaitAsync` / `PublishMessageAndWaitAsync`) | Waits for async cascades (outbox stage → consumer handler → side effects) to settle before assertion. |
| **Coverlet** (via `--collect "XPlat Code Coverage"`) | Cobertura XML coverage measurement, per-test-project. |
| **reportgenerator** | Aggregates per-project Cobertura into a single markdown summary in the CI job summary. |
| **BenchmarkDotNet** | Microbenchmarks at [benchmarks/NextAurora.Benchmarks](../benchmarks/NextAurora.Benchmarks). |
| **k6** | Load smoke at [scripts/k6/smoke.js](../scripts/k6/smoke.js). |

### Integration slices (four today)

| Service | Container(s) | What it proves |
|---|---|---|
| **CatalogService** | Postgres + Redis | HybridCache invalidation, `xmin` concurrency, gRPC server, search projection |
| **OrderService** | SQL Server (Wolverine stubbed) | Outbox staging, `RowVersion` concurrency, saga publish-side, read projection |
| **PaymentService** | SQL Server (Wolverine stubbed) | Acceptor→Gateway split (long-running-work-on-the-bus pattern), outbox staging, idempotency, `RowVersion` concurrency |
| **ShippingService** | Postgres (Wolverine stubbed) | IDOR-safe read predicate, saga consume-side handler, `xmin` concurrency, idempotency under at-least-once delivery |

Wire-level coverage (ASB round-trip) is intentionally not part of this rung —
see Gap 1.

### Required-test patterns

These are hard rules from [CLAUDE.md "Testing"](../CLAUDE.md#testing), not
suggestions. Every PR-review pass checks for them.

| Pattern | When required | Why required |
|---|---|---|
| **AAA with narrative comments** | Every test. `// ARRANGE` / `// ACT` / `// ASSERT` all-caps with em-dash explanation; multi-invariant ASSERTs numbered with rationale. | A junior dev reads one test top-to-bottom and understands the contract + failure mode without reading the SUT. Reference templates: [ProductAuthorizationTests.cs](../tests/CatalogService.Tests.Integration/ProductAuthorizationTests.cs), [OrderSagaTests.cs](../tests/OrderService.Tests.Integration/OrderSagaTests.cs), [OrderTests.cs](../tests/OrderService.Tests.Unit/Domain/OrderTests.cs). |
| **IDOR test** | Every new endpoint that returns or mutates a scoped entity | Authenticate as buyer X, request a resource owned by buyer Y, assert **404 (not 403)**. Absence of this test is exactly how the original `GET /api/v1/orders/{id}` IDOR survived undetected for the lifetime of the codebase. |
| **Outbox-in-non-handler test** | Code paths publishing events from outside a Wolverine handler (BackgroundService sweepers, recovery jobs) | Assert a row appears in `wolverine.outgoing_envelopes` in the same transaction as the entity write. The PaymentRecoveryJob outbox bug survived because no test asserted that. |
| **Handler-DI-registration check** | Any integration test using `scope.ServiceProvider.GetRequiredService<MyHandler>()` | Wolverine's handler-discovery does NOT populate `IServiceCollection` — handlers resolved directly in tests must be `AddScoped<MyHandler>()`'d in `AddXInfrastructure`. Failure mode: `InvalidOperationException` on first test run. Catch at PR review, not in CI. |

---

## Stage 4 — PR-time (GitHub Actions)

| Workflow | Purpose |
|---|---|
| [.github/workflows/ci.yml](../.github/workflows/ci.yml) | Build + unit tests (with Codecov upload) + concurrency-audit grep + integration tests (with Codecov upload). NuGet cache. `concurrency: cancel-in-progress` on the workflow. |
| [.github/workflows/codeql.yml](../.github/workflows/codeql.yml) | CodeQL SAST. `security-and-quality` query set. Weekly + on PR. |
| [.github/dependabot.yml](../.github/dependabot.yml) | NuGet weekly (grouped per ecosystem), GitHub Actions monthly. |
| [.github/workflows/deploy-catalog-demo-fly.yml](../.github/workflows/deploy-catalog-demo-fly.yml) | Deploy CatalogService.Api to Fly.io (primary path). |
| [.github/workflows/deploy-catalog-demo.yml](../.github/workflows/deploy-catalog-demo.yml) | AWS App Runner alternative (scaffolded, not actively used). |

### PR-side configuration
| File | Role |
|---|---|
| [.github/PULL_REQUEST_TEMPLATE.md](../.github/PULL_REQUEST_TEMPLATE.md) | "How it was built" (AI vs hand-written) + Verification sections to keep PR claims honest. |
| [.github/AI_WORKFLOW.md](../.github/AI_WORKFLOW.md) | Companion to README's "How it was built" — exact tools, guardrails, what's deliberately NOT used. |
| [.github/copilot-instructions.md](../.github/copilot-instructions.md) | Copilot-side conventions. |
| [.coderabbit.yaml](../.coderabbit.yaml) | CodeRabbit per-path instructions encoding THIS project's conventions. Requires CodeRabbit GitHub App installed. |

### `.coderabbit.yaml` `path_instructions` — surface #2 of the encoding loop

`.coderabbit.yaml` is one of the five surfaces from "The reflexive step" — it's
how a rule encoded once in CLAUDE.md gets re-checked at PR-review time without
re-deriving it. Today's glob set (~12 entries) covers:

- `**/Features/**` — VSA shape (command/validator/handler co-location, no
  cross-feature dependencies).
- `**/Domain/**/*.cs` — aggregate factory methods, `Guid.CreateVersion7()`
  for IDs, no `I*Repository` wrapper interfaces.
- `**/Endpoints/**` — `MapV1ApiGroup`, `.RequireAuthorization()` on
  non-public endpoints, 404-not-403 for IDOR, rate-limiting on
  search/payment endpoints.
- `**/*RecoveryJob*.cs` — the outbox-outside-handler atomicity trap (explicit
  `BeginTransactionAsync` → `PublishAsync` → `SaveChangesAsync` → `CommitAsync`).
- `**/*Migration*.cs` — migrations are immutable once applied.
- `**/*Test*.cs` — AAA narrative comments, IDOR test required, AwesomeAssertions
  fluent style.
- `NextAurora.ServiceDefaults/**` — middleware order, JWT
  `ClockSkew = 30s`, OpenTelemetry exporters.
- A few others (refer to the file).

When a new rule earns surface #2, add a glob if no existing one fits.

### Reviewers
| Reviewer | Strengths | Limits |
|---|---|---|
| **CodeRabbit** | LLM-based, reads diffs, picks up cross-file consistency, missing tests, naming drift. Project-specific via `path_instructions`. | Not deterministic — same diff can produce different findings. Profile "assertive" surfaces more findings than "chill". |
| **Codecov** | Coverage trend, per-file deltas, PR-level coverage report. Free OSS tier. | Doesn't *gate* PRs without explicit threshold config (currently no gate — see Gaps). |
| **CodeQL** | Static security analysis. Hosted by GitHub. | C# rule set is broad but generic — not project-specific. |
| **dorny/test-reporter** | Surfaces TRX test results as a PR check run instead of buried in job logs. | Just reporting; no analysis. |
| **architecture-reviewer agent** | Project-specific, applies CLAUDE.md rules. Invoked manually for non-trivial architectural changes. Prompts for encodings as Step 7. | Doesn't auto-fire on PR. |

CodeRabbit fires automatically on every PR; the architecture-reviewer is
invoked manually when an architectural pattern is in play. **Not redundant —
different cadence, different lens.**

---

## Stage 5 — Merge / Runtime

| Tool | Role |
|---|---|
| **.NET Aspire** | Local dev orchestration. `dotnet run --project NextAurora.AppHost` brings up all services + Postgres + SQL Server + Service Bus emulator + Redis + Keycloak in one command. Aspire dashboard at http://localhost:18888. |
| **OpenTelemetry** | Traces + metrics + logs throughout. Aspire ingests in dev; Application Insights ingests in prod. |
| **Wolverine** | In-process message bus + transactional outbox. Adapter for Azure Service Bus. |
| **Scalar UI** | Interactive API docs at `/scalar/v1` per service (dev-only). |
| **Fly.io** | CatalogService demo at https://catalog-api-demo.fly.dev. Single Machine, auto-stops when idle. |
| **CorrelationId middleware** (in [NextAurora.ServiceDefaults](../NextAurora.ServiceDefaults/)) | Correlation/User/Session ID propagation across HTTP + Service Bus boundaries. |

---

## Loop comparison: spec-driven vs rule-encoding

A natural comparison is **spec-driven development** in its 2026 incarnations —
[GitHub Spec Kit](https://github.com/github/spec-kit) (`/specify` + `/plan` +
`/tasks`), [Kiro](https://kiro.dev/) (`.kiro/specs/*.md` driving
spec→design→tasks→code traceability), `AGENT.md` / `AGENTS.md` rule-file
patterns, and the older OpenAPI-spec-first / TDD-as-spec lineages. All of
them encode invariants once so future work doesn't re-derive them. Same
lineage; different end of the stick.

| Axis | Spec-driven | This project's rule-encoding loop |
|---|---|---|
| What's authored upfront | A per-feature spec markdown describing requirements + acceptance criteria | A canonical *invariants* file (CLAUDE.md). No per-feature spec. |
| Source of truth | The spec — code is downstream and regeneratable | Rules + code together. Rules describe the code's invariants; code embodies them. |
| How drift is caught | Spec/code traceability checks; the agent regenerates from spec on mismatch | `/check-rules` audit + `See CLAUDE.md.` paraphrase hook + CodeRabbit `path_instructions` + architecture-reviewer Pattern Checklist + `TreatWarningsAsErrors` |
| Cost shape | High upfront authoring cost per feature; cheap regeneration | ~Zero upfront per feature; rule-encoding cost amortizes across all future sessions |
| Failure mode | Spec rot — spec gets stale, code drifts, nobody trusts the spec; OR spec ceremony for trivial changes | Rule sprawl, or encoding fatigue ("not worth writing this one down") |
| Best fit | Greenfield features with stable requirements, multi-agent handoffs, regulated domains where the spec IS the audit trail | Evolving codebases, single-team or solo dev, areas where the rules emerge from doing |
| Agent's role | Spec author + plan generator + code generator (three phases, each gated) | Code generator + rule encoder (one phase, with a reflexive write-the-lesson-down step) |

**Honest takeaway**: this project's loop and spec-driven development are the
same lineage seen from different ends — both encode invariants once so future
work doesn't re-derive them — but the invariants here are about
codebase-wide architectural *shape*, not per-feature requirements. CLAUDE.md
plays the AGENT.md role; per-feature "specs" live in PR descriptions,
validator records, and the failing-test-as-spec discipline already mandated
by the `test-driven-development` skill. Grafting Spec Kit / Kiro mechanics
into the routine loop would convert the compounding speed advantage into
ceremony.

**The one place spec-driven elements might earn their keep**: a future
`/specify-saga` skill for multi-service saga features (the Hetzner full-saga
deployment work), where the contract genuinely does want to be committed-to
before the first endpoint lands. That belongs in Gaps, not here.

---

## Gaps — and the pragmatic solution for each

The gaps below are real. Each one is sized for how much the *actual* problem
warrants — not how much could theoretically be done.

### Gap 1 — Cross-service E2E over the real Azure Service Bus wire is not tested

**What's missing:** All four integration slices use a stubbed Wolverine
transport. The actual `OrderPlacedEvent` → ASB → PaymentService consumer
round-trip is uncovered.

**Pragmatic solution:** Defer until needed. The stubbed-transport tests cover
the load-bearing correctness (handler logic, outbox staging, EF + concurrency
tokens, idempotency); the wire itself mostly exercises Microsoft's ASB
emulator + Wolverine's adapter — the fragile last mile, not the architecture.
When this slice does land, gate it as a **manual nightly job**
(`workflow_dispatch:` or `schedule:` once a day), not every PR — the ASB
emulator container wants an MSSQL sidecar and adds ~3 minutes per run. Not
worth that tax per-PR.

### Gap 2 — No production performance baselines

**What's missing:** BenchmarkDotNet + k6 harness exists but has never run
under realistic concurrent traffic. We can't tell "fast enough" from "lucky
so far."

**Pragmatic solution:** Pick exactly two endpoints to baseline —
`GET /api/v1/products/{id}` (read-heavy hot path) and `POST /api/v1/orders`
(saga entry point). k6 at 100 concurrent users, capture P50/P95/P99 +
GC-pause distribution (`dotnet-counters` for `System.Runtime`) + HybridCache
hit ratio. Commit to `docs/perf-baselines.md` (not yet created). Re-measure
quarterly or on perf-sensitive PRs. Don't baseline everything.

### Gap 3 — `.claude/settings.json` accumulates session cruft

**What's missing:** Claude Code's auto-permission-grant flow saves narrow
per-command allow entries during active sessions. Over a busy session,
settings.json bloats with 30+ one-off entries.

**Pragmatic solution:** Don't build a hook. Just
`git restore .claude/settings.json` periodically — every commit, basically.
The durable wildcard entries (`Bash(dotnet *)`, `Bash(git *)`) are stable;
the one-offs are noise. If this gets annoying enough to measure, add an
8-line `Stop` hook that strips entries not in a curated whitelist. Don't
write it yet.

### Gap 4 — GitHub Actions are version-pinned (`@vN`), not SHA-pinned

**What's missing:** Supply-chain hardening best practice is to pin actions
to immutable commit SHAs so a maintainer can't change what runs by
re-tagging.

**Pragmatic solution:** Stick with `@vN` tags + Dependabot Actions weekly
updates as the layered defense (a tag move would be detected by Dependabot
within ~24h). If higher assurance is needed, run
[pin-github-action](https://github.com/mheap/pin-github-action) once to
SHA-pin everything in a single hardening PR — *all six actions* at once.
Inconsistent pinning is the worst of both worlds.

### Gap 5 — No coverage gate

**What's missing:** Codecov shows the badge + trend, but doesn't fail PRs
when coverage drops.

**Pragmatic solution:** Add `codecov.yml` at repo root with
`coverage.status.project: target: auto, threshold: 1%`. Lets normal PRs
through but fails ones that drop coverage by >1%. Don't set absolute
thresholds (they create perverse incentives — delete uncovered code
instead of testing it).

### Gap 6 — AppHost smoke run is manual

**What's missing:** [scripts/smoke-test.sh](../scripts/smoke-test.sh) verifies
service liveness, versioning, auth flow, order placement — but only runs when
someone remembers to invoke it.

**Pragmatic solution:** Add `workflow_dispatch:` job against the Fly demo (or
self-hosted runner — heavy). Skip per-PR; trigger nightly via `schedule:`
cron OR manually when investigating a deployment regression. Aspire boot is
60+ seconds — not worth per-PR.

### Gap 7 — No secret scanning beyond CodeQL

**What's missing:** CodeQL covers SAST but doesn't dedicated-scan for
hardcoded secrets, leaked keys, or known-vulnerable dependency CVEs beyond
what Dependabot catches.

**Pragmatic solution:** Add one GitHub Action:
[`gitleaks/gitleaks-action@v2`](https://github.com/gitleaks/gitleaks-action).
Five-line workflow, free for public repos. Pair with a quarterly run of
`dotnet list package --vulnerable` (5-line shell script) for CVE deps.

### Gap 8 — Production migration deploy step not automated

**What's missing:** `MigrateDatabaseAsync` only runs in `Development`.
Production migrations require manual `dotnet ef database update`.

**Pragmatic solution:** This is the *right* design — auto-migrating on prod
startup is dangerous (one bad migration takes down all replicas
simultaneously). Keep manual run, but add a separate `deploy-migrate`
GitHub Actions job that runs against the production connection string,
gated by a manual-approval environment (`environment: production-migration`
with required reviewers). Solves the automation gap without losing safety.

### Gap 9 — Distributed rate-limiting is per-instance only

**What's missing:** `GET /api/v1/products/search` and
`POST /api/v1/payments/process` use ASP.NET Core's in-memory
`AddFixedWindowLimiter`. Once any service runs 2+ instances, the effective
rate is N× the limit.

**Pragmatic solution:** NextAurora is single-instance everywhere today
(Catalog deployed; the rest local), so the in-memory limiter is correct
*for now*. When the saga-deployed services scale out, swap affected
endpoints to a Redis-backed limiter using the project's existing Redis
(present for HybridCache). Critical: use a Lua `EVAL` for the
INCR + EXPIRE pair — the two-op sequence has a race window under
concurrency. Auditing this is a Phase 3 deliverable in
[docs/full-saga-deployment-plan.md](full-saga-deployment-plan.md).

### Gap 10 — A `/specify-saga` skill could earn its keep, eventually

**What's missing:** Multi-service saga features (the Hetzner full-saga
deployment work) genuinely want a contract committed-to before the first
endpoint lands. The single-slice case is covered by
`/new-feature-slice`; the multi-slice case isn't.

**Pragmatic solution:** Don't build it yet. The first concrete need (cart
+ checkout + abandoned-cart-drip, or similar) will tell us what shape the
skill should have. A pre-built `/specify-saga` skill before that lesson
would be Spec Kit ceremony grafted onto a loop that doesn't need it. See
"Loop comparison" above for why this is the *one* spec-driven element
worth keeping in mind, and the only one.

---

## What we deliberately don't use

These are tools considered and skipped, for the record. (See
[.github/AI_WORKFLOW.md "What I don't use AI for"](../.github/AI_WORKFLOW.md)
for the curation rationale.)

| Tool | Why skipped |
|---|---|
| **SonarCloud** (hosted dashboard) | Overlap with existing SonarAnalyzer.CSharp at build time. Codecov badge gives the trend signal; SonarCloud would add a dashboard without much new detection. |
| **DependenSee** (project dep graph SVG) | The architecture map serves the same purpose for AI consumption. May add later if a human-facing diagram becomes useful. |
| **SonarQube** (self-hosted) | Self-hosting infrastructure overhead doesn't pay back at this project size. |
| **Frontend testing tools** (Playwright, etc.) | Storefront + SellerPortal are static-file scaffolds — no frontend to test. |
| **MCP servers** | Not building an MCP server. |
| **CI/CD pipeline generator skills** | Existing CI works; adding a generator is anti-pragmatic. |
| **Differential-review skill** (trailofbits) | Direct overlap with architecture-reviewer agent + CodeRabbit. |
| **Spec Kit `/specify` + `/plan` + `/tasks` flow** | Per-feature spec authoring would convert this project's compounding-rule speed advantage into ceremony. See "Loop comparison" — the rule-encoding loop is the dual, not a replacement. |

---

## Lineage and grounding

The encoding loop isn't a new idea. It's the latest iteration of a long lineage of iterative + invariant-encoding methodologies:

- **Larman & Basili 2003 (IEEE Computer)** — iterative development goes back to the 1950s; the single-pass document-driven ideal was doubted from the start, even by Royce
- **Ostroff, Makalsky, Paige 2004 (XP)** — *"Agile Specification-Driven Development"* argued *"a 'complete' specification is a flawed ideal"* and specs should emerge as tests and contracts
- **METR 2025 controlled trial** — experienced developers were measurably slower with AI but felt faster. *"Being wrong while feeling fast is the whole failure in one sentence"*
- **Anthropic September 2025 postmortem** — ~30% of Claude Code users hit at least one degraded response over a five-week period; most never noticed (the Dependence Debt example)
- **OpenAI Symphony (April 2026)** — 2,169-line RFC-grade formal spec was distilled FROM software built first (Codex generated every line). *"The deep, RFC-grade spec is retrospective documentation of software that already ran."* The industry sells the output of that process as if it were the method
- **Uber 2026** — burned through annual AI coding budget in ~4 months, $500-$2,000/engineer/month at 84% Claude Code adoption. CTO publicly said the team was "back to the drawing board." Cost meter pricing upward, value link *"not there yet"*
- **Kapil Viren Ahuja's IDSD series (Activated Thinker, March–May 2026)** — the most thoroughly worked-out parallel: Intent + Context + Expectations (ICE), with the **Agentic Iron Triangle** reshape — Speed → table stakes, Quality → floor held by evals, Cost → split into **tokens + cognition**. Per-feature methodology. The encoding loop's contribution is the cross-feature compounding IDSD doesn't address

### The decay warning (every method eventually decays toward what it replaced)

Per Ahuja's own admission about IDSD: *"Maybe IDD decays into a thirty-field intent form, and we land right back here, a new name on the door."* Agile decayed into spec-process-with-shorter-cycles. SDD started as goal-direction and decayed into 1,300-line specs. TDD decayed into coverage-gaming.

The encoding loop's defense against decay:

1. **Lean-CLAUDE.md discipline + CI size budget** (soft 400, hard 500) — prevents the canon itself from bloating into the spec it was meant to replace
2. **`/check-rules` audit** — paraphrases stay aligned with the canonical source
3. **The explicit Continuous Rule Encoding compounding step** — keeps "lessons from this feature" distinct from "what this feature must do," so the rule-set never collapses into per-feature ceremony
4. **The enforcement spectrum** — promotes rules from convention → automation → mechanical, so high-value rules become loud failures, not silent conventions

### The encoding loop's contribution to the lineage

Explicit **cross-feature compounding** via the 5 encoding surfaces + the enforcement spectrum + the disciplines that catch drift (file-move, doc-and-diagram, lean-CLAUDE.md, presence-in-loop, continue-is-the-verb, broken-link audit). Every feature makes the next one start smarter — which is the only thing that scales when generation cost falls to zero and cognition stays finite.

---

## Source links

- [CLAUDE.md](../CLAUDE.md) — canonical project rules
- [docs/STATUS.md](STATUS.md) — cross-session entry point
- [docs/architecture.md](architecture.md) — services + communication patterns
- [docs/performance-and-data-correctness.md](performance-and-data-correctness.md) — the *why* behind every CLAUDE.md performance rule
- [.claude/architecture-map.md](../.claude/architecture-map.md) — code-graph for AI + humans
- [.claude/README.md](../.claude/README.md) — agent vs slash command vs skill decision tree
- [.github/AI_WORKFLOW.md](../.github/AI_WORKFLOW.md) — the "how" of AI-assisted work
- [README.md "How it was built"](../README.md) — the surface story
