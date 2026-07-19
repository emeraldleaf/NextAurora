#!/usr/bin/env bash
# PreToolUse hook on Bash. When the command opens a PR (`gh pr create`), surfaces a reminder
# to run the architecture-reviewer agent first for architecturally-significant changes.
#
# Purpose: the "architecture-reviewer before merge" rule (CLAUDE.md "Presence in the loop")
# proved skippable as principle alone — the ASB->RabbitMQ swap (PR #159) shipped to a PR before
# any review; the review, run only after a prompt, then caught a dead OpenTelemetry trace source.
# This is the mechanical catch at the ship moment. Non-blocking by design: significance is a
# judgment call (pattern-conforming changes legitimately skip the agent), and a hard deny on a
# command pattern can't ever let the PR through. The gate is "resolve findings before MERGE",
# and PRs are not auto-merged, so a reminder at open-time is correctly timed.
#
# See CLAUDE.md "Architecturally-significant changes get an architecture-reviewer pass".

set -uo pipefail

command=$(jq -r '.tool_input.command // ""' 2>/dev/null)

# `git commit` messages routinely mention "gh pr create" in prose (this script's own commit did),
# which would false-positive on a plain substring match. A real PR-open is never part of a commit.
case "$command" in
    *"git commit"*) exit 0 ;;
esac

# Only fire when actually opening a PR.
case "$command" in
    *"gh pr create"*) ;;
    *) exit 0 ;;
esac

# Feed the reminder text straight into jq as raw stdin (-R -s) to build the hook output.
# (Avoids a heredoc inside $(...), which mis-parses apostrophes on macOS bash 3.2.)
jq -Rs '{hookSpecificOutput: {hookEventName: "PreToolUse", additionalContext: .}}' <<'EOF'
ARCHITECTURE-REVIEW GATE (CLAUDE.md "Presence in the loop") -- about to open a PR.

If this change is architecturally significant, run the `architecture-reviewer` agent on the
diff and address or explicitly defer its findings in the PR body before merge. Significant =
  - adds/removes a dependency or transport, OR
  - touches 3+ services, OR
  - alters a cross-cutting pattern (DI, middleware, persistence, messaging, auth), OR
  - modifies a Domain aggregate, OR
  - the /feature-spec Significance Check would flag it.

Pattern-conforming changes (CRUD matching an existing shape, an audit-log row, a doc-only
edit) may proceed without the agent. This is a reminder, not a block.
EOF
