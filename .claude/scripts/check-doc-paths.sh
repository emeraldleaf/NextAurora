#!/usr/bin/env bash
# Doc-path audit — backtick-quoted repo paths and #L line anchors in markdown must resolve.
#
# Companion to ci.yml's broken-link audit (which checks [md](links) to local source files).
# This covers the two surfaces the 2026-08-30 drift sweep found rotting with no gate:
# inline `code` paths (15 findings taught the retired {Service}.Api layout) and #L line
# anchors (8 findings pointed at lines that had moved).
#
# A backtick token counts as a repo path when it contains a slash, carries a known source
# extension (or a trailing slash), and has no glob/placeholder characters. It passes if it
# exists from the repo root, relative to the doc's own directory, or as the suffix of any
# tracked file (so `Features/PlaceOrder.cs` inside a service walkthrough still resolves).
# Line anchors pass when the target file has at least that many lines.
#
# Allowlist: .claude/doc-paths-allowlist.txt — one file per line, for historical records
# whose old paths are the point. Keep it short; prefer fixing the reference.
#
# Usage: .claude/scripts/check-doc-paths.sh   (CI runs it per PR)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
python3 - <<'PYEOF'
import os, re, subprocess, sys

ALLOW = ".claude/doc-paths-allowlist.txt"
allow_files, allow_tokens = set(), set()
if os.path.exists(ALLOW):
    for line in open(ALLOW):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(None, 1)
        if len(parts) == 2:
            allow_tokens.add((parts[0], parts[1]))
        else:
            allow_files.add(parts[0])

tracked = subprocess.run(["git", "ls-files"], capture_output=True, text=True).stdout.split("\n")
tracked_set = set(tracked)
mds = [f for f in tracked if f.endswith(".md") and f not in allow_files
       and not f.startswith(".claude/skills/")]  # vendored skills carry other repos' example paths

EXTS = ("cs csproj props targets md yml yaml json sh proto svg excalidraw ts tsx js jsx toml "
        "sql txt lock conf html css slnx gif jpg png py editorconfig gitignore").split()
BAD_CHARS = set("{}$*<>()?~\"' ")

def resolves(tok, mddir):
    for cand in (tok, os.path.normpath(os.path.join(mddir, tok))):
        if os.path.exists(cand):
            return True
    # suffix match against tracked files (service-relative mentions like Features/PlaceOrder.cs)
    suffix = "/" + tok.rstrip("/")
    return any(t.endswith(suffix) or t == tok.rstrip("/") for t in tracked)

fail = 0
seen = set()
for f in mds:
    text = open(f, encoding="utf-8", errors="replace").read()
    # strip fenced code blocks: their paths are illustrative snippets, already audited by eye
    prose = re.sub(r"```.*?```", "", text, flags=re.S)
    mddir = os.path.dirname(f) or "."
    for m in re.finditer(r"`([^`\n]+)`", prose):
        tok = m.group(1)
        # need at least one interior slash: bare folder mentions like `Features/` are
        # service-relative shorthand, not repo paths
        if "/" not in tok.rstrip("/") or "://" in tok or tok.startswith(("http", "mailto:", "/", "~")):
            continue
        if BAD_CHARS & set(tok):
            continue
        base = tok.rstrip("/")
        ext = base.rsplit(".", 1)[-1].lower() if "." in base.split("/")[-1] else ""
        if not (tok.endswith("/") or ext in EXTS):
            continue
        key = (f, tok)
        if key in seen or key in allow_tokens:
            continue
        seen.add(key)
        if not resolves(tok, mddir):
            line = text[: text.find("`" + tok + "`")].count("\n") + 1
            print(f"::error file={f},line={line}::backtick path does not resolve: {tok}")
            fail = 1
    for m in re.finditer(r"\]\(([^)#\s]+)#L(\d+)(?:-L(\d+))?\)", text):
        target, start, end = m.group(1), int(m.group(2)), int(m.group(3) or m.group(2))
        cand = os.path.normpath(os.path.join(mddir, target))
        if not os.path.exists(cand):
            continue  # existence is the broken-link audit's job
        try:
            n = sum(1 for _ in open(cand, encoding="utf-8", errors="replace"))
        except OSError:
            continue
        if end > n:
            line = text[: m.start()].count("\n") + 1
            print(f"::error file={f},line={line}::line anchor #L{end} beyond end of {target} ({n} lines)")
            fail = 1

print("Doc-path audit clean." if not fail else "Doc-path audit FAILED.")
sys.exit(fail)
PYEOF
