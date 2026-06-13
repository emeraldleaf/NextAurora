# Skill bucket organization — `.claude/commands/` registry

Slash commands for this repo, organized by purpose (bucket). Pattern stolen
from [Matt Pocock's skills repo](https://github.com/mattpocock/skills) —
buckets keep things findable as the registry grows past ~5 commands.

**Why this doc lives in `docs/` not `.claude/commands/`:** Claude Code scans
every `.md` file in `.claude/commands/` as a slash command. A `README.md`
there registers `/README` as a command. Index files belong in `docs/`.

**All commands in `.claude/commands/` are user-invoked**
(`disable-model-invocation: true` in frontmatter). They are explicit rituals;
the model won't auto-fire them on description match. To run one, type
`/command-name` in Claude Code.

## Buckets

### `engineering/` — daily code work

Skills that touch source code, tests, architecture, or CI.

| Command | Purpose |
|---|---|
| [`/feature-spec`](../.claude/commands/feature-spec.md) | Draft a structured feature spec — value gate, gap check, hand-off to scaffolding. The ritual at the start of any non-trivial work |
| [`/new-feature-slice`](../.claude/commands/new-feature-slice.md) | Scaffold a new VSA feature slice (command + validator + handler) in a service |
| [`/check-rules`](../.claude/commands/check-rules.md) | Audit "See CLAUDE.md" cross-references for drift against the canonical rules |

### `productivity/` — non-code workflow tools

Skills that help capture, communicate, or organize work *about* the code.

| Command | Purpose |
|---|---|
| [`/article-audit`](../.claude/commands/article-audit.md) | Audit an article (URL or pasted) against CLAUDE.md surfaces — output a coverage map + verdict + (if gap) a draft issue body |
| [`/grid-infographic`](../.claude/commands/grid-infographic.md) | Generate a premium technical thought-leadership infographic (Pandey-style numbered grid + central hero hub) via Gemini Nano Banana Pro |
| [`/sync-status`](../.claude/commands/sync-status.md) | Refresh `docs/STATUS.md` from recent git activity + open issues |

---

## When to add to which bucket

- **`engineering/`** — if the command's *output* is source code, tests, CI
  config, architecture rules, or anything in the build pipeline
- **`productivity/`** — if the command's output is documentation, an audit
  report, an issue draft, a visual, a status update, or anything *about*
  the work rather than the work itself
- **`personal/`** *(future)* — if it's tied to your own setup and shouldn't
  appear in the bucket README
- **`in-progress/`** *(future)* — drafts not yet ready to ship
- **`deprecated/`** *(future)* — kept for reference but no longer current

Currently we use flat layout (no nested directories) because Claude Code's
slash-command discovery is filename-based. Once the registry grows past
~10 commands, we'll evaluate whether to physically split into subdirectories
or just keep the convention as a documentation discipline.

## Conventions for adding a command

1. **Pick the right bucket** above.
2. **Set `disable-model-invocation: true`** in frontmatter — these are
   rituals, not assistive guesses. If you want an auto-fired skill instead,
   put it in `.claude/skills/` not here.
3. **Description is the user-facing summary** — one line, clear about what
   the command produces.
4. **`argument-hint:` if it takes args** — helps autocomplete.
5. **Add to this doc** under the relevant bucket. PRs editing
   `.claude/commands/` should update both the command file and this index.
6. **Cap the SKILL body around 300 lines.** If longer, split into a paired
   `command-name/REFERENCE.md` (same pattern as our `CLAUDE.md` →
   `docs/*.md` decomposition).

## Related

- [`CONTEXT.md`](../CONTEXT.md) — domain vocabulary used by commands here
  (terms like *encoding loop*, *5 surfaces*, *3 tiers*, *value gate*)
- [`CLAUDE.md`](../CLAUDE.md) — canonical rules that commands cite via
  `See CLAUDE.md.` paraphrases
- [`.claude/skills/`](../.claude/skills/) — auto-invokable skills (different
  registration model — those CAN fire on description match)
- [`.claude/agents/`](../.claude/agents/) — subagent definitions (architecture-reviewer)
