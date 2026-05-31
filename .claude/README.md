# `.claude/` — repository AI workflow configuration

This directory holds the configuration that shapes how Claude Code interacts with this repository. Everything here is checked into git so the same workflow runs for every developer (and for AI assistants that pick up work in a fresh session).

If you just want the canonical project rules, those live in [`/CLAUDE.md`](../CLAUDE.md), not here. This directory holds the *tooling* — agents, slash commands, skills, hooks, and the architecture map — that enforces and amplifies those rules.

For the visual + narrative of how all this plumbing fits together, see [`docs/dev-loop.md`](../docs/dev-loop.md).

---

## Directory layout

```
.claude/
├── agents/                  Specialized subagents (Agent tool)
├── architecture-map.md      Repo-wide code-graph for AI orientation
├── commands/                Slash commands (/command-name)
├── scripts/                 Hook scripts wired in settings.json
├── settings.json            Permissions, hooks, additional dirs
├── settings.local.json      Local-only overrides (gitignored)
└── skills/                  On-demand specialized knowledge (Skill tool)
```

---

## The three extension surfaces — agents, commands, skills

These three look superficially similar but solve different problems. Picking the right one matters because each has a different invocation cost + cognitive overhead.

### Quick decision tree

```
Is the work a recurring motion you want as a single keystroke?
├── YES → slash command in .claude/commands/
└── NO  → Does it need a focused, independent reviewer
         that won't share your conversation context?
         ├── YES → subagent in .claude/agents/
         └── NO  → Is it specialized knowledge the model
                   should pull in on demand?
                   ├── YES → skill in .claude/skills/
                   └── NO  → It's probably a CLAUDE.md rule,
                             not an extension surface.
```

### Agents (`.claude/agents/`)

**What:** Standalone subagents the main conversation can dispatch. They start fresh with no shared context — only the prompt + the files they read. They produce a report; you decide what to do with it.

**When to use:**
- Independent review or audit work (architecture, security, code review)
- Long-running research that would bloat the main conversation
- Parallel work that doesn't depend on the main conversation state
- Anything that benefits from "fresh eyes" — i.e. *not* primed by the conversation that surfaced the question

**When NOT to use:**
- Quick iterative back-and-forth (use the main conversation)
- Anything that needs the conversation's prior context
- Tasks where the user expects to interactively steer

**How to invoke:**
- From Claude Code: the model calls the Agent tool with `subagent_type: <agent-name>` and a self-contained prompt
- Agents you dispatch run in their own context window with the tools listed in their frontmatter

**Authoring:**
- A markdown file in `.claude/agents/` with YAML frontmatter: `name`, `description`, `tools` (the tools the agent can use)
- The body is the system prompt for the subagent — its job, how it works, output format, hard rules
- The `description` field is critical — Claude Code surfaces it when deciding which agent to dispatch

**Current inventory:**
| Agent | Purpose |
|---|---|
| `architecture-reviewer.md` | Reviews a file/diff against this project's SOLID/DDD/VSA-vs-Clean/Performance/Security rules from CLAUDE.md. Produces a categorized report (Must fix / Should consider / Aligned / Rules to encode). Best invoked with a specific file path. |

### Commands (`.claude/commands/`)

**What:** Markdown files that define `/command-name` slash commands. They bake repeated motions into a single keystroke + a consistent procedure. Run in the main conversation (no separate context).

**When to use:**
- Frequently-repeated procedure with multiple steps that should be done the same way each time
- Refusable patterns (e.g. "scaffold a VSA feature slice — but refuse for CatalogService because it uses Clean Architecture")
- Audit motions (`/check-rules`)
- Status / project-state updates (`/sync-status`)

**When NOT to use:**
- One-off tasks (just ask in chat)
- Things that need fresh context (use an agent)
- Procedures so trivial they don't warrant a name

**How to invoke:**
- Type `/command-name` in Claude Code chat
- Optional positional arguments expand into `$ARGUMENTS` in the command body
- The command's markdown body becomes the prompt for the next assistant turn — so you can include instructions, hard rules, file references, etc.

**Authoring:**
- A markdown file in `.claude/commands/` with YAML frontmatter: `description` (one-line) and `argument-hint` (shown in chat when typing `/`)
- The body is the prompt: tell the model what to do, what to read, what to refuse, what to output

**Current inventory:**
| Command | What it does |
|---|---|
| `/new-feature-slice <ServiceName> <FeatureName>` | Scaffolds a VSA feature slice matching the `PlaceOrder.cs` canonical shape. Refuses for CatalogService (Clean Architecture). |
| `/sync-status` | Refreshes `docs/STATUS.md` from `git log` + open issues, diff-style with confirmation. |
| `/check-rules` | Audits every `See CLAUDE.md` paraphrase against the canonical rule. |

### Skills (`.claude/skills/`)

**What:** Specialized knowledge bundles the model loads on demand. Unlike agents (which run in a separate context), a skill's content gets *injected into the main conversation* when triggered — so the model can apply specialized procedures, vocabularies, or decision criteria mid-task.

**When to use:**
- Procedural knowledge that doesn't apply to every conversation (so it shouldn't be in CLAUDE.md, which is always loaded)
- Cross-cutting expertise (debugging discipline, performance tuning, test-driven development)
- Externally-authored bundles you want to install once (community skills from `obra/superpowers`, `trailofbits/skills`, etc.)
- Domain-specific reference material (e.g. dotnet-performance for EF Core hot-path review)

**When NOT to use:**
- Universal project rules (those go in CLAUDE.md)
- One-off procedures (just describe in chat)
- Things narrow enough to be a slash command

**How to invoke:**
- **Manual:** the model calls the Skill tool with `skill: <skill-name>` based on the user's request
- **Auto-trigger:** Claude Code reads each skill's frontmatter `description` field and matches against user-message intent. If the description says "Use when ... debugging ... bug ... test failure ...", the skill auto-loads when the user reports a bug.
- A skill's full contents (`SKILL.md` + referenced files) become part of the model's working context for the rest of the conversation

**Authoring:**
- A directory in `.claude/skills/<skill-name>/` containing at minimum `SKILL.md` with YAML frontmatter
- `name` (kebab-case, matches directory)
- `description` (load-bearing — this is what triggers auto-invocation; include trigger keywords)
- Optional sub-files (`references/`, `scripts/`) referenced from `SKILL.md`

**Current inventory — when to invoke each:**

Skills only earn their keep if you *notice the moment* to invoke them. The
table below names the **trigger phrase** — the specific thought, symptom, or
word that should make you reach for the skill — instead of an abstract
"when it fires." Memorize the moments, not the skills.

Grouped by how the dev-loop actually uses them:

**Tier 1 — Actively used (load on demand)**

| Skill | Trigger phrase (the catch-yourself moment) |
|---|---|
| `dotnet-performance` | *"I'm about to touch an EF query / async path / cache / migration / middleware."* Load it **before** writing the code, not after. Also the canonical reference target for CLAUDE.md "Performance Rules." |
| `excalidraw-diagram` | *"A picture would make this argument load-bearing"* — explaining an architecture verbally and realizing the flow is hard to follow without one. Or: an existing diagram has drifted from reality. |
| `writing-plans` + `executing-plans` | *"This will take more than one session, has reviewable checkpoints, and the wrong direction is expensive to undo."* Big tell: >3 files + >1 architectural decision. Canonical use: the Hetzner full-saga deployment plan. |

**Tier 2 — Ambient (discipline absorbed into CLAUDE.md; skill is the reach-for surface)**

| Skill | Trigger phrase (the catch-yourself moment) |
|---|---|
| `verification-before-completion` | You're about to type *"should work,"* *"I think this is right,"* or *"done"* — but you haven't actually run it. The word **"should"** is the signal. |
| `systematic-debugging` | You've made **two attempts** at a bug and neither worked. Or: you can describe the *symptom* but not the *mechanism*. Random-fix loop → load this. |
| `test-driven-development` | *Greenfield logic with non-trivial branching* (state machine, authorization predicate, calculation with edge cases). OR: fixing a bug — write the **failing test that reproduces it FIRST**, then fix. |
| `variant-analysis` | *"I just fixed one — are there siblings?"* Any time a fix lands or the architecture-reviewer flags something, ask: "is this a *category*?" If yes → load. |

**Tier 3 — Dormant by design (fire only on specific triggers)**

| Skill | Trigger phrase (the catch-yourself moment) |
|---|---|
| `using-git-worktrees` | *"I need to work on two things in parallel without context-switch friction"* — reviewing a PR mid-feature, running a long benchmark while editing, or the agent's about to do something risky and you want the main tree untouched. |
| `skill-security-auditor` | *"I'm about to install a community skill from a repo I don't fully trust."* Run this on its `SKILL.md` **before** install, not after. |

**The meta-pattern.** Most triggers are catch-yourself moments — a specific
phrase you're about to use or a behavior you're about to do. The phrases that
should trip the wire:

- *"should work"* → `verification-before-completion`
- *"two attempts and still broken"* → `systematic-debugging`
- *"I just fixed one — are there more?"* → `variant-analysis`
- *"this will take a while"* → `writing-plans`
- *"I'm about to write a query/cache/migration"* → `dotnet-performance`

---

## Hooks (`.claude/scripts/`)

Hooks are Bash scripts wired in `settings.json` that fire on Claude Code events. They run in the harness (not in the model's context) and can block tool calls, inject context, or audit changes.

**Current hooks:**
| Hook | Event | Script |
|---|---|---|
| Block sync-over-async | `PreToolUse` (Edit/Write on `.cs`) | `scripts/block-sync-over-async.sh` |
| Inject STATUS.md | `SessionStart` | `scripts/inject-status.sh` |
| Cross-reference paraphrases | `PostToolUse` (Edit/Write on CLAUDE.md) | `scripts/check-claude-md-refs.sh` |

Hooks live in `settings.json` because they need to be discoverable + version-controlled per repo. They're not described in detail here — see the script files for what each does and the canonical [`docs/dev-loop.md`](../docs/dev-loop.md) "Edit-time" section for the workflow context.

---

## The architecture map (`architecture-map.md`)

A structured, AI-consumable map of the repository: services, their shapes (Clean vs VSA), what database each owns, event flow, port interfaces, aggregates, concurrency tokens. Read by the `architecture-reviewer` agent before every review; useful for humans too.

This file is canonical for **structure** (what's where). The canonical rules are in [`/CLAUDE.md`](../CLAUDE.md). When services/aggregates/events change materially, refresh the map (the file has regen commands at the bottom).

---

## When to add a new extension

Per CLAUDE.md "Continuous Rule Encoding": when a review surfaces an antipattern or rule worth encoding, it goes into the appropriate `.claude/` surface in the same session:

| Finding shape | Goes in |
|---|---|
| A pattern detection that should fire on every PR touching `X/*.cs` files | `.coderabbit.yaml` `path_instructions` (file-pattern-scoped) |
| A multi-step procedure that's run repeatedly with consistent shape | `.claude/commands/<name>.md` (slash command) |
| A specialized review/audit that needs fresh context | `.claude/agents/<name>.md` (subagent) |
| Specialized knowledge that the model loads on demand | `.claude/skills/<name>/SKILL.md` (skill) |
| A hard rule with no narrow-trigger | `/CLAUDE.md` (the canonical rule file) |
| An automated reaction to a Claude Code event | `settings.json` hook + `.claude/scripts/<name>.sh` |

A fix PR + the corresponding `.claude/` encoding should land together. Per CLAUDE.md "A merged fix PR without the corresponding rule is a half-finished job."
