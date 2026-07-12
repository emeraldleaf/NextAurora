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

# AI-instruction surfaces are ALWAYS on the review list, marker or not — so this list is
# built BEFORE any no-match early-exit. They paraphrase canon for other AI tools (Copilot,
# CodeRabbit), and drift there silently regenerates retired patterns —
# .github/copilot-instructions.md still taught the removed transport months after the swap
# because it carried no marker. See CLAUDE.md.
ai_surfaces=""
for f in "$REPO_ROOT/.github/copilot-instructions.md" "$REPO_ROOT/.coderabbit.yaml"; do
    [ -f "$f" ] && ai_surfaces="${ai_surfaces}${f}"$'\n'
done

# Nothing at all to remind about: silent no-op.
if [ -z "$matches" ] && [ -z "$ai_surfaces" ]; then
    exit 0
fi

# Emit additionalContext via hookSpecificOutput so the model sees the reminder.
msg=$(printf 'CLAUDE.md was edited. Files containing the "See CLAUDE.md" marker (review each for staleness against the new rule):\n%s\n\nAI-instruction surfaces (ALWAYS check these — they paraphrase canon for other AI tools and drift silently):\n%s' "${matches:-(none)}" "$ai_surfaces")
jq -n --arg m "$msg" '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $m}}'
