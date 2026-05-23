#!/usr/bin/env bash
# SessionStart hook. Surfaces the top of docs/STATUS.md so the assistant starts each
# session knowing where the project is and what's next, instead of cold-reading the repo.
#
# CLAUDE.md points to STATUS.md as the cross-session entry point. This hook automates
# the "read STATUS.md first" rule that lives in the project_session_entry_point memory.

set -uo pipefail

REPO_ROOT="/Users/joshuadell/NovaCraft"
STATUS="$REPO_ROOT/docs/STATUS.md"

# Silent no-op if STATUS.md is missing (e.g. fresh clone before the doc lands).
if [ ! -f "$STATUS" ]; then
    exit 0
fi

# Take the header + the "Where we are" section. Cap at 80 lines so we don't flood context
# on every session start — full STATUS.md is one Read tool call away if more detail is needed.
snippet=$(head -80 "$STATUS")

# Branch + last commit give the assistant orientation that STATUS.md alone doesn't.
branch=$(git -C "$REPO_ROOT" branch --show-current 2>/dev/null || echo "(detached)")
last_commit=$(git -C "$REPO_ROOT" log -1 --oneline 2>/dev/null || echo "(no commits)")

msg=$(printf '## Session orientation\n\nBranch: %s\nLast commit: %s\n\n--- docs/STATUS.md (top 80 lines) ---\n%s\n--- end snippet ---\n\nFull STATUS.md at docs/STATUS.md. Update it at the start or end of each working session per CLAUDE.md.' "$branch" "$last_commit" "$snippet")

jq -n --arg m "$msg" '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $m}}'
