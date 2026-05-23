# Development Loop — Tooling & Configuration

A pragmatic inventory of every tool, hook, agent, and config that shapes how
code lands in this repo, organized by *when* in the dev loop each one fires.

Visual companion: [dev-loop.svg](dev-loop.svg) (source: [dev-loop.excalidraw](dev-loop.excalidraw)).

The bar this document is held to: **describe what's actually in place** (with
file paths so you can verify), **call out gaps honestly**, and **for each gap,
propose a pragmatic solution sized to the actual problem** — no
build-a-whole-new-system suggestions.

---

## Stage 1 — Edit-time (IDE + Claude Code)

This is where most code originates. The tooling here shapes proposed edits
*before* they land in a file.

### Canonical rules
| File | Role |
|---|---|
| [CLAUDE.md](../CLAUDE.md) | 25 KB of opinionated rules — SOLID/DDD/VSA-vs-Clean, performance, security, conventions, debugging discipline. Loaded into every Claude Code session. |
| [.editorconfig](../.editorconfig) | Naming + formatting enforced by Roslyn at build time. |
| [Directory.Build.props](../Directory.Build.props) | Shared build settings (TreatWarningsAsErrors, target framework, analyzers). |
| [Directory.Packages.props](../Directory.Packages.props) | Central package management — versions live here, csproj files have no version attributes. |
| [BannedSymbols.txt](../BannedSymbols.txt) | Banned APIs (`Task.WaitAll`, `Parallel.For`, `Thread.Sleep`, etc.) enforced by `BannedApiAnalyzers`. |

### Hooks ([.claude/scripts/](../.claude/scripts/))
| Hook | Event | What it does |
|---|---|---|
| [block-sync-over-async.sh](../.claude/scripts/block-sync-over-async.sh) | `PreToolUse` (Edit\|Write on .cs) | Rejects `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` *in proposed edits*. Build-time net (BannedSymbols.txt) catches the same patterns later — this hook catches them earlier so the bad diff never lands. |
| [inject-status.sh](../.claude/scripts/inject-status.sh) | `SessionStart` | Injects top of STATUS.md + current branch + last commit so sessions don't start cold. |
| [check-claude-md-refs.sh](../.claude/scripts/check-claude-md-refs.sh) | `PostToolUse` (Edit\|Write on CLAUDE.md) | When CLAUDE.md changes, lists every file containing the `See CLAUDE.md` paraphrase marker so drift can be reviewed. |

### Slash commands ([.claude/commands/](../.claude/commands/))
| Command | Purpose |
|---|---|
| [/new-feature-slice](../.claude/commands/new-feature-slice.md) | Scaffolds a VSA feature slice matching the [OrderService/Features/PlaceOrder.cs](../OrderService/Features/PlaceOrder.cs) canonical shape. Refuses for CatalogService (Clean Architecture). |
| [/sync-status](../.claude/commands/sync-status.md) | Refreshes STATUS.md from `git log` + open issues. |
| [/check-rules](../.claude/commands/check-rules.md) | Audits every `See CLAUDE.md` paraphrase against the canonical rule. |

### Agents ([.claude/agents/](../.claude/agents/))
| Agent | Purpose |
|---|---|
| [architecture-reviewer](../.claude/agents/architecture-reviewer.md) | Loads CLAUDE.md + [architecture-map.md](../.claude/architecture-map.md), evaluates a target against SOLID/DDD/VSA-vs-Clean/Performance rules. Reports only — no edits. |

### Skills ([.claude/skills/](../.claude/skills/))
| Skill | Source | When it fires |
|---|---|---|
| dotnet-performance | this repo | Writing handlers, queries, repositories, middleware, migrations |
| excalidraw-diagram | this repo | Generating diagrams (this doc's diagram, in fact) |
| skill-security-auditor | [alirezarezvani/claude-skills](https://github.com/alirezarezvani/claude-skills) | Pre-install security gate. Audits any skill before install. |
| verification-before-completion | [obra/superpowers](https://github.com/obra/superpowers) | About to claim "done/fixed/passing" — forces evidence. |
| systematic-debugging | [obra/superpowers](https://github.com/obra/superpowers) | Any bug, test failure, unexpected behavior. Four-phase root cause discipline. |
| variant-analysis | [trailofbits/skills](https://github.com/trailofbits/skills) | One bug found — search every similar pattern across the codebase. |
| test-driven-development | [obra/superpowers](https://github.com/obra/superpowers) | Implementing a feature or bugfix — RED-GREEN-REFACTOR. |
| using-git-worktrees | [obra/superpowers](https://github.com/obra/superpowers) | Feature work that needs workspace isolation. |
| writing-plans + executing-plans | [obra/superpowers](https://github.com/obra/superpowers) | Spec-driven multi-step task with review checkpoints. |

### Architecture map
[.claude/architecture-map.md](../.claude/architecture-map.md) — code-graph for AI + humans. Services, shapes (Clean vs VSA), event flow, ports, aggregates, concurrency tokens.

### Secondary AI reviewer
[GitHub Copilot](https://github.com/features/copilot) (GPT-5) in-editor for second-opinion diff review. Conventions encoded in [.github/copilot-instructions.md](../.github/copilot-instructions.md). The principle: disagreement between Claude and Copilot is a signal to dig deeper, not pick the louder voice.

---

## Stage 2 — Build-time (`dotnet build`)

Static analysis that runs as part of every build. `TreatWarningsAsErrors` is on; zero warnings allowed.

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

| Tool | Purpose |
|---|---|
| **xunit** | Test runner. |
| **AwesomeAssertions** | Fluent assertion library (drop-in fork of FluentAssertions 8 — migrated off ahead of FA's paid license). |
| **NSubstitute** | Mocking for unit tests. |
| **Microsoft.AspNetCore.Mvc.Testing** + `WebApplicationFactory` | In-process API hosting for integration tests. |
| **Testcontainers** | Real DB / Redis / messaging via Docker for integration tests. macOS uses `~/.docker/run/docker.sock`; CI uses standard path. |
| **Coverlet** (via `--collect "XPlat Code Coverage"`) | Cobertura XML coverage measurement, per-test-project. |
| **reportgenerator** | Aggregates per-project Cobertura into a single markdown summary in the CI job summary. |
| **BenchmarkDotNet** | Microbenchmarks at [benchmarks/NextAurora.Benchmarks](../benchmarks/NextAurora.Benchmarks). |
| **k6** | Load smoke at [scripts/k6/smoke.js](../scripts/k6/smoke.js). |

Two integration slices today: **CatalogService** (Postgres + Redis) and **OrderService** (SQL Server + stubbed Wolverine transport).

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
| [.coderabbit.yaml](../.coderabbit.yaml) | CodeRabbit per-path instructions encoding THIS project's conventions (VSA vs Clean, `MapV1ApiGroup`, async/CancellationToken, EF migrations immutable, etc.). Requires CodeRabbit GitHub App installed. |

### Reviewers
| Reviewer | Strengths | Limits |
|---|---|---|
| **CodeRabbit** | LLM-based, reads diffs, picks up cross-file consistency, missing tests, naming drift. Project-specific via per-path instructions. | Not deterministic — same diff can produce different findings. Profile "assertive" surfaces more findings than "chill". |
| **Codecov** | Coverage trend, per-file deltas, PR-level coverage report. Free OSS tier. | Doesn't *gate* PRs without explicit threshold config (currently no gate — see Gaps). |
| **CodeQL** | Static security analysis. Hosted by GitHub. | C# rule set is broad but generic — not project-specific. |
| **dorny/test-reporter** | Surfaces TRX test results as a PR check run instead of buried in job logs. | Just reporting; no analysis. |
| **architecture-reviewer agent** | Project-specific, applies CLAUDE.md rules. Invoked manually. | Doesn't auto-fire on PR — must be triggered. |

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

## Gaps — and the pragmatic solution for each

The gaps below are real. Each one is sized for how much the *actual* problem warrants — not how much could theoretically be done.

### Gap 1 — Cross-service E2E over the real Azure Service Bus wire is not tested

**What's missing:** Integration tests today use a stubbed Wolverine transport. The actual `OrderPlacedEvent` → ASB → PaymentService consumer round-trip is uncovered.

**Pragmatic solution:** Defer until needed. The stubbed-transport tests cover the load-bearing correctness (handler logic, outbox staging, EF + concurrency tokens); the wire itself mostly exercises Microsoft's ASB emulator + Wolverine's adapter — the fragile last mile, not the architecture. When this slice does land, gate it as a **manual nightly job** (`workflow_dispatch:` or `schedule:` once a day), not every PR — the ASB emulator container wants an MSSQL sidecar and adds ~3 minutes to every run. Not worth that tax per-PR.

### Gap 2 — No production performance baselines

**What's missing:** BenchmarkDotNet + k6 harness exists but has never run under realistic concurrent traffic. We can't tell the difference between "fast enough" and "lucky so far."

**Pragmatic solution:** Pick exactly two endpoints to baseline — `GET /api/v1/products/{id}` (read-heavy hot path) and `POST /api/v1/orders` (the saga entry point). Run a k6 profile at 100 concurrent users, capture P50/P95/P99 + GC-pause distribution (`dotnet-counters` for `System.Runtime`) + HybridCache hit ratio. Commit the numbers to `docs/perf-baselines.md` (file not yet created) as the baseline. Re-measure quarterly or on perf-sensitive PRs. Don't try to baseline everything — pick the two highest-traffic endpoints, baseline once, move on.

### Gap 3 — `.claude/settings.json` accumulates session cruft

**What's missing:** Claude Code's auto-permission-grant flow saves narrow per-command allow entries during active sessions. Over a busy session, settings.json bloats with 30+ one-off entries.

**Pragmatic solution:** Don't build a hook. Just `git restore .claude/settings.json` periodically — every commit, basically. The durable wildcard entries (`Bash(dotnet *)`, `Bash(git *)`, etc.) are stable; the one-offs are noise. If this becomes too annoying, add a Stop hook (8-line bash script) that strips any allow entry not in a curated whitelist on session end. **Don't write the hook yet** — the manual restore is fine until the friction is measurable.

### Gap 4 — GitHub Actions are version-pinned (`@vN`), not SHA-pinned

**What's missing:** Supply-chain hardening best practice is to pin actions to immutable commit SHAs so a maintainer can't change what runs by re-tagging.

**Pragmatic solution:** Stick with `@vN` tags + Dependabot Actions weekly updates as the layered defense (a tag move would be detected by Dependabot within ~24h). If higher assurance is needed, run [pin-github-action](https://github.com/mheap/pin-github-action) once to SHA-pin everything in a single hardening PR — *all six actions* (`actions/checkout`, `actions/setup-dotnet`, `actions/cache`, `dorny/test-reporter`, `github/codeql-action/*`, `codecov/codecov-action`), not one at a time. Inconsistent pinning is the worst of both worlds.

### Gap 5 — No coverage gate

**What's missing:** Codecov shows the badge + trend, but doesn't fail PRs when coverage drops.

**Pragmatic solution:** Add a `codecov.yml` at repo root (file not yet created) with `coverage.status.project: target: auto, threshold: 1%`. That lets normal PRs through but fails ones that drop coverage by >1%. Don't set absolute thresholds (e.g. "must be 80%") — they create perverse incentives (delete uncovered code instead of testing it). Relative threshold = "don't make it worse."

### Gap 6 — AppHost smoke run is manual

**What's missing:** [scripts/smoke-test.sh](../scripts/smoke-test.sh) verifies service liveness, versioning, auth flow, order placement — but only runs when someone remembers to invoke it.

**Pragmatic solution:** Add a `workflow_dispatch:` job that runs against the Fly demo (or spins up Aspire in a self-hosted runner — heavy). Skip per-PR; trigger nightly via `schedule:` cron OR manually when investigating a deployment regression. **Not worth running on every PR** — Aspire boot is 60+ seconds even with cache, and most PRs don't change the smoke surface.

### Gap 7 — No secret scanning beyond CodeQL

**What's missing:** CodeQL covers SAST but doesn't dedicated-scan for hardcoded secrets, leaked keys, or known-vulnerable dependency CVEs beyond what Dependabot catches.

**Pragmatic solution:** Add one GitHub Action: [`gitleaks/gitleaks-action@v2`](https://github.com/gitleaks/gitleaks-action). Five-line workflow, free for public repos, scans every PR for secret-looking patterns. Pair with a quarterly run of `dotnet list package --vulnerable` (5-line shell script) for CVE deps. Both are low-effort additions.

### Gap 8 — Production migration deploy step not automated

**What's missing:** `MigrateDatabaseAsync` only runs in `Development` environment. Production migrations require manual `dotnet ef database update`.

**Pragmatic solution:** This is the *right* design — auto-migrating on prod startup is dangerous (a bad migration takes down all replicas simultaneously). Keep the manual run, but add a **separate `deploy-migrate` GitHub Actions job** that runs `dotnet ef database update --no-build` against the production connection string, **gated by a manual approval environment** (`environment: production-migration` with required reviewers). Solves the automation gap without losing the safety.

### Gap 9 — CodeRabbit + architecture-reviewer agent feel redundant

**What's missing:** Both review code on PRs. The overlap is real.

**Pragmatic solution:** Keep both — they catch different things. CodeRabbit is rules-based (cross-file consistency, naming drift, missing tests, generic .NET hygiene). The architecture-reviewer agent is rule-*application* (does this slice respect THIS project's SOLID/DDD/VSA-vs-Clean rules using CLAUDE.md as canon?). Not redundant — complementary. The signal you're looking for is: CodeRabbit fires automatically on every PR; the architecture-reviewer is invoked manually for *non-trivial architectural changes*. Different cadence, different lens.

### Gap 10 — No "this PR is AI-generated" tagging in commits

**What's missing:** The PR template asks the author to declare AI involvement, but commits don't carry it (beyond Claude Code's `Co-Authored-By:` line, which not every contributor uses).

**Pragmatic solution:** Don't add a tag. The PR template covers the disclosure layer; commit-level tagging would create noise + ask contributors to remember another convention. The Co-Authored-By line that Claude Code adds automatically is sufficient signal where it's used.

---

## What we deliberately don't use

These are tools considered and skipped, for the record. (See [.github/AI_WORKFLOW.md "What I don't use AI for"](../.github/AI_WORKFLOW.md) for the curation rationale.)

| Tool | Why skipped |
|---|---|
| **SonarCloud** (hosted dashboard) | Overlap with existing SonarAnalyzer.CSharp at build time. Codecov badge gives the trend signal; SonarCloud would add a dashboard without much new detection. |
| **DependenSee** (project dep graph SVG) | Considered for the architecture-map; not implemented yet. The architecture map serves the same purpose for AI consumption. May add later if a human-facing diagram becomes useful. |
| **SonarQube** (self-hosted) | Self-hosting infrastructure overhead doesn't pay back at this project size. |
| **Frontend testing tools** (Playwright, etc.) | Storefront + SellerPortal are static-file scaffolds — no frontend to test. |
| **MCP servers** | Not building an MCP server. |
| **CI/CD pipeline generator skills** | Existing CI works; adding a generator is anti-pragmatic. |
| **Differential-review skill** (trailofbits) | Direct overlap with the architecture-reviewer agent + CodeRabbit. |

---

## Source links

- [CLAUDE.md](../CLAUDE.md) — canonical project rules
- [docs/STATUS.md](STATUS.md) — cross-session entry point
- [docs/architecture.md](architecture.md) — services + communication patterns
- [.claude/architecture-map.md](../.claude/architecture-map.md) — code-graph for AI + humans
- [.github/AI_WORKFLOW.md](../.github/AI_WORKFLOW.md) — the "how" of AI-assisted work
- [README.md "How it was built"](../README.md) — the surface story
