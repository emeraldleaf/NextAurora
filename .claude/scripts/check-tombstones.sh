#!/usr/bin/env bash
# Tombstone audit — removed-identifier drift control. See CLAUDE.md "Identifier-move discipline".
#
# When a subsystem is removed (a transport, a metric, an API), its identifiers get tombstoned in
# .claude/tombstones.txt. This script fails if any tombstoned pattern appears in tracked files
# outside the allowlist (.claude/tombstones-allowlist.txt).
#
# Why: the compiler catches stale identifiers in code; NOTHING catches them in docs, comments,
# and config. The RabbitMQ swap (#159) left 15+ docs teaching Azure Service Bus as current —
# found by an ultracode review, not by the loop. This closes that gap mechanically: the sweep's
# completion criterion is "this script passes", not "the docs someone remembered are updated".
#
# GROUP-SCOPED EXEMPTIONS: patterns live in groups ([group-name] headings); an allowlist line is
# `<group> <path>` and exempts that file from that group ONLY. A file allowlisted for a past
# removal is still audited against every future tombstone. (The original file-scoped allowlist
# exempted docs/architecture.md from ALL later tombstones, so the audit passed while it still
# documented deleted artifacts as live — found by architecture review, not by this script.)
#
# KNOWN BLIND SPOT: identifiers split across a line break evade these regexes — in .excalidraw
# JSON a wrapped label is literally "messages.\nabandoned", which `messages\.abandoned` cannot
# match. When a tombstoned name might appear in a DIAGRAM, check the rendered .png/.svg too —
# the render is the only surface that sees wrapped text.
#
# Usage: .claude/scripts/check-tombstones.sh   (CI runs it per PR)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

TOMBSTONES=".claude/tombstones.txt"
ALLOWLIST=".claude/tombstones-allowlist.txt"

fail=0
group=""

while IFS= read -r line; do
    [ -z "$line" ] && continue
    case "$line" in \#*) continue ;; esac

    # `[group-name]` starts a new group.
    case "$line" in
        \[*\])
            group="${line#[}"
            group="${group%]}"
            continue
            ;;
    esac

    pattern="$line"
    if [ -z "$group" ]; then
        echo "TOMBSTONE CONFIG ERROR — pattern '$pattern' appears before any [group] heading."
        exit 1
    fi

    # Build this pattern's exclusions: files allowlisted for THIS group only.
    excludes=()
    while IFS= read -r a; do
        [ -z "$a" ] && continue
        case "$a" in \#*) continue ;; esac
        # shellcheck disable=SC2086
        set -- $a
        [ "$#" -ge 2 ] || continue
        if [ "$1" = "$group" ]; then
            excludes+=(":(exclude)$2")
        fi
    done < "$ALLOWLIST"

    # git grep exit codes: 0 = matches (violation), 1 = clean, >=2 = error (e.g. invalid regex).
    # An invalid tombstone must FAIL the audit, not silently disable itself.
    set +e
    hits=$(git grep -inE "$pattern" -- '.' "${excludes[@]}" 2>&1)
    status=$?
    set -e

    if [ "$status" -ge 2 ]; then
        echo "TOMBSTONE AUDIT ERROR — [$group] pattern '$pattern' failed to evaluate (git grep exit $status):"
        echo "$hits" | sed -n '1,5p'
        fail=1
    elif [ "$status" -eq 0 ] && [ -n "$hits" ]; then
        echo "TOMBSTONE VIOLATION — [$group] pattern '$pattern':"
        # sed -n rather than `| head`: under pipefail, head closing the pipe early would
        # SIGPIPE the producer and abort the script mid-audit.
        echo "$hits" | sed -n '1,30p' | sed 's/^/  /'
        echo ""
        fail=1
    fi
done < "$TOMBSTONES"

if [ "$fail" -eq 1 ]; then
    echo "Removed identifiers are resurfacing (or were never fully swept)."
    echo "Fix the references — or, for a genuinely historical/comparative mention, add"
    echo "'<group> <path>' to $ALLOWLIST with a justification comment."
    exit 1
fi
echo "Tombstone audit clean."
