#!/usr/bin/env bash
# Rebuild diagram .png + .svg outputs from .excalidraw sources.
#
# Walks docs/*.excalidraw, regenerates the sibling .png + .svg for each file
# whose source is newer than its outputs (or whose outputs don't exist).
# Skips files that are already up-to-date.
#
# Usage:
#   .claude/scripts/rebuild-diagrams.sh           # incremental (only stale)
#   .claude/scripts/rebuild-diagrams.sh --force   # force-rebuild everything
#   .claude/scripts/rebuild-diagrams.sh path.excalidraw  # one file
#
# Requires the excalidraw-diagram skill's Playwright setup. If you've never
# rendered before:
#   cd .claude/skills/excalidraw-diagram/references
#   uv sync
#   uv run playwright install chromium

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RENDER_SCRIPT="$REPO_ROOT/.claude/skills/excalidraw-diagram/references/render_excalidraw.py"
DOCS_DIR="$REPO_ROOT/docs"

if [ ! -f "$RENDER_SCRIPT" ]; then
    echo "ERROR: render script not found at $RENDER_SCRIPT" >&2
    exit 1
fi

FORCE=0
TARGETS=()
for arg in "$@"; do
    case "$arg" in
        --force|-f) FORCE=1 ;;
        *.excalidraw) TARGETS+=("$arg") ;;
        *) echo "ignoring unknown arg: $arg" >&2 ;;
    esac
done

# If no explicit targets, walk every .excalidraw under docs/.
if [ ${#TARGETS[@]} -eq 0 ]; then
    while IFS= read -r -d '' f; do
        TARGETS+=("$f")
    done < <(find "$DOCS_DIR" -maxdepth 2 -name "*.excalidraw" -type f -print0 2>/dev/null)
fi

if [ ${#TARGETS[@]} -eq 0 ]; then
    echo "No .excalidraw files found under $DOCS_DIR/"
    exit 0
fi

# A file is stale if either output (.png or .svg) is older than the source, or
# either output doesn't exist.
is_stale() {
    local src="$1"
    local base="${src%.excalidraw}"
    local png="${base}.png"
    local svg="${base}.svg"
    [ ! -f "$png" ] && return 0
    [ ! -f "$svg" ] && return 0
    [ "$src" -nt "$png" ] && return 0
    [ "$src" -nt "$svg" ] && return 0
    return 1
}

rebuilt=0
skipped=0
failed=0
cd "$REPO_ROOT/.claude/skills/excalidraw-diagram/references"
for src in "${TARGETS[@]}"; do
    rel="${src#$REPO_ROOT/}"
    if [ "$FORCE" -eq 0 ] && ! is_stale "$src"; then
        echo "  [skip] $rel (up-to-date)"
        skipped=$((skipped + 1))
        continue
    fi
    echo "  [build] $rel"
    if uv run python "$RENDER_SCRIPT" "$src" >/dev/null 2>&1; then
        rebuilt=$((rebuilt + 1))
    else
        echo "    FAILED — try running the command directly to see the error:"
        echo "    uv run python $RENDER_SCRIPT $src"
        failed=$((failed + 1))
    fi
done

echo
echo "Rebuilt: $rebuilt   Skipped: $skipped   Failed: $failed"

# Exit non-zero if anything failed so this script is CI-friendly.
[ "$failed" -eq 0 ]
