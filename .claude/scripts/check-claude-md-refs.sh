#!/usr/bin/env bash
# PostToolUse hook helper. Reads the tool-call JSON on stdin; if the edited file is
# the repo-root CLAUDE.md, prints a list of files containing the marker "See CLAUDE.md"
# (excluding CLAUDE.md itself) as additionalContext for the assistant.
#
# The list represents files that paraphrase a CLAUDE.md rule and may need review when
# the canonical rule changes. Convention is documented in CLAUDE.md "Debugging Discipline".

set -uo pipefail

REPO_ROOT="/Users/joshuadell/NovaCraft"
CANONICAL="$REPO_ROOT/CLAUDE.md"

# Extract the file_path from the tool-call payload. Empty if jq fails or field missing.
file=$(jq -r '.tool_input.file_path // ""' 2>/dev/null)

# Only act when the edited file is the repo-root CLAUDE.md. Anything else: silent no-op.
case "$file" in
    "$CANONICAL")
        ;;
    *)
        exit 0
        ;;
esac

# Find files with the cross-reference marker, excluding CLAUDE.md itself.
matches=$(grep -rln "See CLAUDE.md" \
    --include='*.cs' --include='*.props' --include='*.csproj' --include='*.md' \
    "$REPO_ROOT" 2>/dev/null \
    | grep -v "^${CANONICAL}\$" \
    || true)

# No matches: silent no-op (nothing to remind about).
if [ -z "$matches" ]; then
    exit 0
fi

# Emit additionalContext via hookSpecificOutput so the model sees the reminder.
msg=$(printf 'CLAUDE.md was edited. Files containing the "See CLAUDE.md" marker (review each for staleness against the new rule):\n%s' "$matches")
jq -n --arg m "$msg" '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $m}}'
