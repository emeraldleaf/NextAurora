---
description: Audit an article (URL or pasted text) against CLAUDE.md and the rule-encoding surfaces — output a coverage map + verdict + (if gap) a draft GitHub issue body
argument-hint: <URL or "pasted">
disable-model-invocation: true
---

# Article audit

The user just pasted (or linked) an external article — a blog post, newsletter,
or talk transcript about a software pattern, performance technique, framework
feature, or architectural practice. Your job is to **audit it against this
project's existing encoding** so the user can decide in one glance: already
covered (skip), or genuine gap (open an issue).

The pattern is established (see #85 consumer-idempotency, #86 sidecar — both
opened via this routine). This command makes the routine reusable.

## Inputs

`$ARGUMENTS` is one of:
- A URL → fetch it with `WebFetch`, then audit the content
- The literal word `pasted` → the article body is in the user's previous
  message; audit that
- Something else → assume it's the article body itself, audit it

## What to do

### 1. Read the article

If URL, fetch it. If pasted, use the prior message content. Don't ask the user
to paste again unless the source is genuinely missing.

### 2. Extract the article's load-bearing claims

Identify 5–10 specific claims the article makes. Bullet form. Examples:

- "Use compiled queries for frequently executed reads"
- "Pass MessageId to broker for publisher-side dedup"
- "Sidecar pattern handles cross-cutting concerns"

Skip throat-clearing, ad copy, and "subscribe to my course" bits.

**Comment threads count.** When the article is a LinkedIn post, Hacker News
submission, blog post with substantive replies, etc., **treat senior-author
or technically-substantive comments as part of the article for claim-
extraction purposes**. Real engineering rules often arrive in the reply
threads rather than the original post — a Microsoft MVP dropping a
load-bearing caveat in a 19-hour-old comment matters as much as the
post's main bullets. Don't dismiss an article as "outside scope" because
the original post is shallow; check whether the comments carry the
substance.

If you can't see the comments (URL fetched, comment thread not in the
response, no pasted thread context), note that in the chat output so the
user knows the audit didn't cover them. Don't fabricate claims from
imagined commenters.

### 3. Map each claim against the project's encoding surfaces

For each claim, search **systematically** through these surfaces in this
order, and quote the matching rule when found:

1. **`/CLAUDE.md`** — canonical hard/soft rules. Use `Grep` for the key
   nouns/verbs (e.g. "compiled queries", "idempotent", "sidecar", "outbox").
2. **`.coderabbit.yaml`** `path_instructions` — file-pattern-scoped guidance.
3. **`.claude/agents/architecture-reviewer.md`** "Pattern Checklist" — scan
   rules the agent applies.
4. **`.claude/skills/dotnet-performance/`** (project-authored) — deeper why
   behind perf rules.
5. **Supporting docs** — read selectively, not exhaustively:
   - `docs/architecture.md`
   - `docs/performance-and-data-correctness.md`
   - `docs/project-decisions.md` (especially for "considered + rejected"
     stances — this is where Dapr/sidecars/etc. live)
   - `docs/cqrs-data-access.md`
   - `docs/dev-loop.md`

Don't read every file end-to-end. Use `Grep` to locate matches, then `Read`
just the relevant sections. The goal is speed.

### 4. Classify each claim

Each claim falls into one of these buckets:

- **"Already encoded, more rigorously"** — the project has a stricter version.
  Quote the project's wording so the comparison is concrete.
- **"Already encoded, equivalent"** — same idea, point at the rule.
- **"Considered + rejected"** — the project explicitly chose otherwise.
  Point at `project-decisions.md` or the relevant CLAUDE.md "do NOT" rule.
- **"Encoded but patterns not consolidated"** — the principle is there, but
  the project's specific implementations aren't named together as a list.
  Worth a small encoding pass. (This was #85 — the three consumer-idempotency
  patterns.)
- **"Implicit, worth making explicit"** — the project follows the practice in
  behavior but doesn't document the *why* or the *triggers*. Worth a small
  doc pass. (This was #86 — sidecar reconsideration triggers.)
- **"Genuine gap"** — the project has no encoded stance. Real work to do.

### 5. Persist the audit to `.claude/audits/`

After producing the chat output (step 6 below), **also write the audit to a
markdown file** under `.claude/audits/YYYY-MM-DD-<slug>.md` where:

- `YYYY-MM-DD` is today's date (use the current session date, not a hardcoded one)
- `<slug>` is a short kebab-case derivation of the article topic
  (e.g. `circuit-breaker`, `csharp-14-extension-members`, `vsa-code-duplication`)

The file holds: article title + author/source + verdict + the comparison
table + the divergence section (if any) + the outcome ("No action" / "Opened
#N" / link to issue body).

Then **append a one-line row to `.claude/audits/INDEX.md`** in the existing
log table. The legend at the top of INDEX.md defines verdict buckets:

- ✅ No action
- ⚙️ Divergence
- 🔧 Consolidation
- 🌱 Gap

**Copyright note.** Audit files contain verbatim quotes from the audited
article. The `.gitignore` excludes `.claude/audits/*` from version control
(see the rule in `.gitignore`) but explicitly allows `INDEX.md` through —
INDEX is just verdict metadata (title + author + bucket + outcome), which
is fair-use commentary, not quoted content. Per-article files stay local-only
on each developer's machine; INDEX.md ships in the repo so the audit log
survives across contributors. You do not need to remove quotes from the
per-article files because they never reach the public repo.

### 6. Ship the INDEX row

INDEX.md is the only file that reaches the repo (per-article audit files
are gitignored). Don't leave the row sitting uncommitted — every previous
audit needed a separate "we need to commit?" prompt and a one-row PR to
finish the job. Roll that into the routine.

**Default — on `main` with only INDEX changed (the common case):**

```bash
SLUG=<the slug from step 5>
git checkout -b chore/audit-log-$SLUG
git add .claude/audits/INDEX.md
git commit -m "docs(audits): log $SLUG audit" -m "<one-line verdict + outcome>"
git push -u origin chore/audit-log-$SLUG
gh pr create --title "docs(audits): log $SLUG audit" --body "Single-row INDEX update for the $SLUG audit. <Verdict bucket>. <Outcome: 'No action' / 'Opened #N' / 'Updated #N in place'>."
gh pr merge --admin --squash --delete-branch
git checkout main && git pull --ff-only
```

Safe because INDEX is single-row append + metadata only, the admin-merge
bypass is configured for exactly this self-merge pattern (precedents:
#106, #107), and the chat output gives the user the verdict before
the merge — they can `gh pr edit` or `gh pr close` if something is off.

**On a feature branch** (rare — audit ran while other work in flight):
just commit the INDEX row to the current branch. It'll ship with the
next push. Mention it in the chat output so it's not forgotten.

**When the audit also opened an issue / PR**: ship the INDEX row *after*
the issue/PR work — the INDEX `Outcome` column references the issue
number (`Opened #N`), so #N has to exist first.

**User signals "wait" or "batch"**: skip the auto-ship; commit on the
current branch (if branching is needed, ask first). Surface the
unshipped row in the chat output. Subsequent audits in the same session
can batch onto the same branch.

### 7. Produce the chat output

Write directly to the user. Structure:

#### Headline verdict

One of:
- **"Already encoded, no action needed."** (most common, ~70%)
- **"Already encoded, with one interesting divergence."** (when the project's
  stance is materially different from the article's recommendation)
- **"Already encoded, with one small consolidation worth doing."** (when the
  patterns are in use but not collected as a canonical list)
- **"Partial coverage — one genuine gap."** (rare)

#### Comparison table

Markdown table: article claim → project encoding → stance.

#### The interesting divergence section (if applicable)

When the project explicitly chose a different path, name the trade-off and
what would flip the calculus. This is where you point at `project-decisions.md`
or the relevant "do NOT" rule.

#### Recommendation

One of:
- **No action** — the table is the deliverable. Done.
- **Open an issue for a small encoding pass** — draft the issue body inline,
  ready to paste into `gh issue create`. Use the project's Issue Form fields
  (What / Why / Acceptance / Notes). Suggest labels.
- **Open an issue for a genuine gap** — same shape, but the work is larger.
  Flag if it's an epic.

If the answer is "open an issue," **first search for an existing one** —
`gh issue list --state open --search "<keywords>"` — and if a live issue
already covers the topic, extend/comment on it instead of opening a
near-duplicate (see CLAUDE.md "Search before you open an issue"). Then **ask
the user before running `gh issue create`.** They might want to tweak wording,
add labels, or defer. Don't just create issues on their behalf.

## Style notes

- Be tight. The user is checking if the article is worth their attention; a
  3-page audit defeats the purpose.
- When quoting CLAUDE.md, use the exact wording. Don't paraphrase the
  canonical rule — that would itself be paraphrase drift.
- Don't suggest opening issues for trivial style nits or restatements of
  things already in `dev-loop.md` "What we deliberately don't use."
- If the article advocates something the project explicitly rejected, the
  table should make that rejection load-bearing — the user should walk away
  knowing the project's stance is intentional, not accidental.

## What this command is NOT

- Not a research assistant. It compares ONE article against the project; it
  doesn't go find related material.
- Not an auto-encoder. It proposes issues; the user decides.
- Not a CodeRabbit replacement. CodeRabbit reviews diffs; this reviews
  external knowledge against the rule set.

## Example invocations

- `/article-audit https://www.milanjovanovic.tech/blog/the-idempotent-consumer-pattern-in-net` — fetches + audits
- `/article-audit pasted` — audits whatever the user pasted in the prior turn
- `/article-audit` (no args) — audits the most recent pasted article in the
  conversation; if there isn't one, ask what to audit

Run the audit on the article identified by `$ARGUMENTS` now.
