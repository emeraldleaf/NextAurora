---
description: Refresh docs/STATUS.md from recent git activity + open issues
---

# /sync-status

Update [docs/STATUS.md](../../docs/STATUS.md) so it accurately reflects where the project is *right now*. STATUS.md is the cross-session entry point per CLAUDE.md — when it goes stale, every future session starts with bad orientation.

## What to do

1. **Read the current STATUS.md** so you know which sections exist and the writing voice.

2. **Gather raw signal** in this order:
   - `git log --oneline -30` — recent commits
   - `git log --since="$(grep -m1 'Last updated:' docs/STATUS.md | sed 's/.*Last updated:[[:space:]]*//')"` — commits since the doc's stamped date
   - `git status --short` — uncommitted work in progress
   - `gh pr list --state all --limit 10` — recent PRs (skip if `gh` isn't authenticated)
   - Open `TODO` / `FIXME` / `HACK` comments in `*.cs` — `grep -rn --include='*.cs' -E 'TODO|FIXME|HACK' . | head -30`

3. **Diff signal against the doc.** For each commit since the stamp date, classify it as:
   - **Landed feature** → should appear under "Recently landed"
   - **Bug fix or doc tweak** → usually skip unless it surfaces a debugging lesson (see CLAUDE.md "Debugging Discipline")
   - **WIP** → "What's next" or "Open issues"

4. **Propose the edit, don't blindly write it.** Output a diff-style preview of what you'd add/move/remove in STATUS.md. Ask the user to confirm before applying.

5. **Update `**Last updated:**`** to today's date when you do apply changes.

## Guardrails

- **Don't lose the existing voice.** STATUS.md is written in opinionated first-person plural ("we landed", "we're not done") — match it.
- **Keep it under ~120 lines.** When sections grow past that, fold old "Recently landed" items into a "## Archive" section at the bottom, or trim them entirely.
- **Don't paraphrase commit messages verbatim.** The commit log is one-line summaries; STATUS.md sentences explain *why this matters* for the next person picking up work.

## Why this command exists

STATUS.md only works if it stays fresh. The mechanical "what landed since the doc was
stamped" diff is the bookkeeping part; the *judgment* part is which commits matter
enough to surface and how to phrase them. This command does the bookkeeping for you so
your attention goes to the judgment.
