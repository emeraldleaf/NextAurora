---
description: Audit "See CLAUDE.md" cross-references for drift against the canonical rules
disable-model-invocation: true
---

# /check-rules

CLAUDE.md is the canonical source of project rules. Paraphrases of those rules live in
docs, READMEs, inline comments, and skill files. Convention: any paraphrase ends with
`See CLAUDE.md` so it's findable. This command audits every paraphrase against the
canonical rule and flags drift.

## What to do

1. **List the paraphrases.** Grep the repo for `See CLAUDE.md` (case-sensitive), excluding CLAUDE.md itself:
   ```bash
   grep -rln "See CLAUDE.md" --include='*.cs' --include='*.md' --include='*.props' --include='*.csproj' . | grep -v '^./CLAUDE.md$'
   ```

2. **For each match**, extract the surrounding sentence/paragraph and the nearest CLAUDE.md rule it paraphrases. The mapping isn't always obvious — match by topic (e.g. a comment mentioning "async on request paths" → CLAUDE.md "Performance Rules" section).

3. **Compare.** For each pair, decide:
   - **Aligned** — the paraphrase agrees with the canonical rule. Report and move on.
   - **Drift** — the paraphrase says something subtly different (older wording, stricter/looser bound, missing nuance). Report with the exact line + the canonical wording, and propose an edit.
   - **Orphan** — no matching rule exists in CLAUDE.md anymore (rule was removed or restructured). Report and ask the user whether to delete the paraphrase or restore the rule.

4. **Print a table** of findings: `file:line  status  topic  suggested action`. Don't auto-apply edits — output diffs and wait for user confirmation per finding.

## Guardrails

- **Treat CLAUDE.md as canonical.** Never propose changing CLAUDE.md to match a paraphrase. If the paraphrase is "better", that's a separate conversation (and a CLAUDE.md edit, which then triggers the cross-reference hook again).
- **One topic at a time.** Don't batch unrelated edits — each finding gets its own confirmation.
- **Skip the PostToolUse hook output.** The hook prints candidate files when CLAUDE.md is edited; this command is the *deeper* audit (reading the rule text, not just listing files).

## Why this command exists

The "See CLAUDE.md" convention is enforced lightly by the PostToolUse hook when CLAUDE.md
is edited — it just lists candidate files. The hook doesn't *read* either side. This
command does. Drift accumulates silently otherwise: an inline comment from six months
ago paraphrases a rule that's since been tightened, and the comment now contradicts the
canon. Catch it on purpose, on a cadence.
