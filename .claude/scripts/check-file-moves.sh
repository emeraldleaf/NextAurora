#!/usr/bin/env bash
# PostToolUse hook on Bash. When the command is `git mv` or `git rm`, finds the source
# path(s) and greps the repo for refs to them. Prints findings as additionalContext so
# the model sees the worklist in the same session the move happened.
#
# Purpose: catch doc/comment/Dockerfile drift in the same session a file gets moved or
# deleted — same compounding loop as check-claude-md-refs.sh but for renames/deletions
# instead of CLAUDE.md edits.
#
# Why git mv / git rm specifically (not plain mv / rm): plain mv/rm is used constantly
# for temp files, build artifacts, and ephemeral work. Restricting to git-tracked
# operations dramatically cuts false-positive noise. The cost is missing the case where
# someone uses plain `mv` on a tracked file — that's the gap CodeRabbit's
# path_instruction for file rename/delete picks up at review time.
#
# See CLAUDE.md "File-move discipline" for the canonical rule.

set -uo pipefail

REPO_ROOT="/Users/joshuadell/NovaCraft"

command=$(jq -r '.tool_input.command // ""' 2>/dev/null)

# Only fire on git mv / git rm. Plain mv/rm is too noisy.
case "$command" in
    *"git mv "*) ;;
    *"git rm "*) ;;
    *) exit 0 ;;
esac

old_paths=()

# git mv <src> <dst>   — capture the FIRST positional arg (the source).
if [[ "$command" =~ git[[:space:]]+mv[[:space:]]+([^[:space:]]+) ]]; then
    src="${BASH_REMATCH[1]}"
    case "$src" in
        -*) ;;  # skip if it looked like a flag
        *) old_paths+=("$src") ;;
    esac
fi

# git rm [flags] <path>...  — capture all non-flag tokens after `git rm`.
if [[ "$command" =~ git[[:space:]]+rm[[:space:]] ]]; then
    args=$(echo "$command" | sed -E 's/^.*git[[:space:]]+rm[[:space:]]+//' | tr -s ' ')
    for token in $args; do
        case "$token" in
            -*) continue ;;
            "") continue ;;
            *) old_paths+=("$token") ;;
        esac
    done
fi

# Nothing to do.
if [ ${#old_paths[@]} -eq 0 ]; then
    exit 0
fi

# For each old path, grep for refs and collect findings. --fixed-strings so paths
# containing dots / slashes match literally instead of as regex.
findings=""
for old_path in "${old_paths[@]}"; do
    case "$old_path" in
        ""|"*"|"."|"./"|".."|"./*") continue ;;
    esac

    matches=$(grep -rln --fixed-strings "$old_path" \
        --include='*.md' --include='*.cs' --include='*.props' --include='*.csproj' \
        --include='*.yml' --include='*.yaml' --include='*.sh' \
        --include='Dockerfile*' \
        "$REPO_ROOT" 2>/dev/null \
        | head -30 \
        || true)
    if [ -n "$matches" ]; then
        findings+=$(printf "Refs to '%s' still present in:\n%s\n\n" "$old_path" "$matches")
    fi
done

if [ -z "$findings" ]; then
    exit 0
fi

msg=$(printf 'File-move/delete detected. Refs to the OLD path may need updating before commit:\n\n%s\nUpdate the paraphrases in the same PR. See CLAUDE.md "File-move discipline".' "$findings")
jq -n --arg m "$msg" '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $m}}'
