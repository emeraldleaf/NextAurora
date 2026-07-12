#!/usr/bin/env bash
# Tombstone audit — removed-identifier drift control. See CLAUDE.md.
#
# When a subsystem is removed (a transport, a metric, an API), its identifiers get
# tombstoned in .claude/tombstones.txt. This script fails if any tombstoned pattern
# appears in tracked files outside the allowlist (.claude/tombstones-allowlist.txt).
#
# Why: the compiler catches stale identifiers in code; NOTHING catches them in docs,
# comments, and config. The RabbitMQ swap (#159) left 15+ docs teaching Azure Service
# Bus as current — found by an ultracode review, not by the loop. This closes that gap
# mechanically: the sweep's completion criterion is "this script passes", not "the docs
# someone remembered are updated".
#
# Usage: .claude/scripts/check-tombstones.sh   (run from anywhere; CI runs it per PR)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

TOMBSTONES=".claude/tombstones.txt"
ALLOWLIST=".claude/tombstones-allowlist.txt"

excludes=()
while IFS= read -r line; do
    [ -z "$line" ] && continue
    case "$line" in \#*) continue ;; esac
    excludes+=(":(exclude)$line")
done < "$ALLOWLIST"

fail=0
while IFS= read -r pattern; do
    [ -z "$pattern" ] && continue
    case "$pattern" in \#*) continue ;; esac
    # git grep over tracked files only; -i case-insensitive, -E extended regex.
    if hits=$(git grep -inE "$pattern" -- '.' "${excludes[@]}" 2>/dev/null) && [ -n "$hits" ]; then
        echo "TOMBSTONE VIOLATION — pattern '$pattern':"
        echo "$hits" | sed 's/^/  /' | head -30
        echo ""
        fail=1
    fi
done < "$TOMBSTONES"

if [ "$fail" -eq 1 ]; then
    echo "Removed identifiers are resurfacing (or were never fully swept)."
    echo "Fix the references — or, for genuinely historical/comparative mentions,"
    echo "add the file to $ALLOWLIST with a justification comment."
    exit 1
fi
echo "Tombstone audit clean."
