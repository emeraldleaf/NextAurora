# Article audit log

Persistent log of every `/article-audit` run. One row per audit; full reasoning lives in the linked file. See [`.claude/commands/article-audit.md`](../commands/article-audit.md) for the routine.

**Why this file exists.** External-knowledge audits used to vanish into chat history. Now each audit writes a markdown file under `.claude/audits/` and appends a one-line index entry below. Greppable, version-controlled, survives across sessions.

## Verdict bucket legend

- ✅ **No action** — already encoded equivalently or more rigorously
- ⚙️ **Divergence** — project chose differently; explicit rejection captured
- 🔧 **Consolidation** — encoded in spirit; small pass worth doing
- 🌱 **Gap** — real work to do; issue opened

## Log

| Date | Article | Verdict | Outcome |
|---|---|---|---|
| 2026-05-31 | [EF Core read query performance](2026-05-31-ef-core-read-performance.md) (Anton Martyniuk) | ✅ All 9 tips already encoded; project stricter on compiled queries + AsSplitQuery | No action |
| 2026-05-31 | [Idempotent Consumer Pattern](2026-05-31-idempotent-consumer.md) (Milan Jovanović) | 🔧 Principle encoded; three project-specific patterns not consolidated | Opened #85 |
| 2026-06-01 | [Sidecar Pattern in microservices](2026-06-01-sidecar-pattern.md) (LinkedIn) | ⚙️ Considered + rejected via Dapr decision (§22); reconsideration triggers not documented | Opened #86 |
| 2026-06-02 | [How to Avoid Code Duplication in VSA](2026-06-02-vsa-code-duplication.md) (Anton Martyniuk) | ✅ Already encoded, often more rigorously | No action |
| 2026-06-02 | [Extension Members in C# 14](2026-06-02-csharp-14-extension-members.md) (Anton Martyniuk) | 🌱 Zero encoded stance; factual C# 13 → C# 14 drift in CLAUDE.md | Opened #88 |
| 2026-06-02 | [Production-Ready ASP.NET Health Checks](2026-06-02-aspnet-health-checks.md) (Milan Jovanović) | ✅ Already encoded with acceptable divergence (liveness/readiness split, Aspire dashboard supersedes UI) | No action |
| 2026-06-02 | [ASP.NET Core Output Cache](2026-06-02-aspnet-output-cache.md) (Anton Martyniuk) | 🔧 Tier silent in project's caching model | Opened #89 |
| 2026-06-03 | [Implementing Circuit Breaker in ASP.NET Core](2026-06-03-circuit-breaker.md) (Kanaiya Katarmal) | ⚙️ Already encoded via `AddStandardResilienceHandler` (§17); article advocates custom Polly pipeline project rejected | No action initially — then opened #97 for the `BRD.md:312` "future: circuit breaker with cached fallback" gap |
| 2026-06-03 | [DI lifetimes / Captive Dependency](2026-06-03-di-lifetimes.md) (LinkedIn) | 🔧 Pattern followed by behavior; no `ValidateScopes`/`ValidateOnBuild` enforcement; captive rule not in CLAUDE.md | Opened #103 |
| 2026-06-03 | [AIDLC vs SDLC framing](2026-06-03-aidlc-vs-sdlc.md) (Anurag Karuparti) | ✅ Already encoded more rigorously via `docs/dev-loop.md` Continuous Rule Encoding loop | No action |
