#!/usr/bin/env bash
# Messaging-topology completeness guard. See CLAUDE.md (messaging trap bullets).
#
# The transport-stubbed integration tests structurally CANNOT catch these regressions
# (they disable AutoProvision and stub routing), and the real-wire failure-injection
# tests (#68) are deferred — so this script is the deterministic floor:
#
#   1. Every MessagingQueues const is declared into the broker topology by at least one
#      publisher (a `.ToQueue(MessagingQueues.X)` call in some Program.cs). A queue const
#      nothing declares means a consumer will bind/listen on a queue whose publisher-side
#      guarantee is missing — the first-boot loss window (#168) reopens.
#   2. Every `ListenToRabbitQueue(...)` is either chained `.ProcessInline()` (store-less
#      services) or lives in a Program.cs that calls `UseDurableInboxOnAllListeners`
#      (store-backed services). A bare listener is buffered: broker acked before the
#      handler runs; a crash loses the buffer (#169).
#   3. No inline string literals in topology calls — names come from
#      MessagingExchanges/MessagingQueues (a typo'd literal is silently auto-provisioned
#      as a new empty object and the consumer starves with no error).
#
# Usage: .claude/scripts/check-messaging-topology.sh   (CI runs it per PR)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

TOPOLOGY="NextAurora.Contracts/Messaging/MessagingTopology.cs"
PROGRAMS=(OrderService/Program.cs PaymentService/Program.cs ShippingService/Program.cs NotificationService/Program.cs)
fail=0

# --- 1. Every queue const reaches a .ToQueue( declaration somewhere -------------------
while IFS= read -r name; do
    if ! grep -q "\.ToQueue(MessagingQueues\.$name)" "${PROGRAMS[@]}"; then
        # Direct queues (no exchange binding) are exempt from .ToQueue() — but their
        # declaration IS the ListenToRabbitQueue call, so that must exist instead;
        # otherwise removing the sole listener leaves an undeclared queue and a clean audit.
        if grep -B2 "public const string $name = " "$TOPOLOGY" | grep -qi "direct queue"; then
            if ! grep -q "ListenToRabbitQueue(MessagingQueues\.$name)" "${PROGRAMS[@]}"; then
                echo "TOPOLOGY GAP — direct queue MessagingQueues.$name has no ListenToRabbitQueue() declaring it"
                fail=1
            fi
            continue
        fi
        echo "TOPOLOGY GAP — MessagingQueues.$name has no .ToQueue() declaration in any Program.cs"
        echo "  (a queue no publisher declares reopens the first-boot loss window — see CLAUDE.md #168)"
        fail=1
    fi
done < <(grep -oE 'public const string [A-Za-z]+' "$TOPOLOGY" | awk '{print $4}' | grep -v 'Events$')

# --- 2. Every listener is durable-inbox or inline --------------------------------------
for f in "${PROGRAMS[@]}"; do
    has_durable_inbox=0
    grep -q "UseDurableInboxOnAllListeners" "$f" && has_durable_inbox=1
    while IFS= read -r line; do
        if [ "$has_durable_inbox" -eq 0 ] && ! echo "$line" | grep -q "\.ProcessInline()"; then
            echo "BARE LISTENER — $f: $line"
            echo "  (buffered listener: broker acked before the handler runs; a crash loses the buffer — see CLAUDE.md #169)"
            fail=1
        fi
    done < <(grep "ListenToRabbitQueue(" "$f" || true)
done

# --- 3. No inline topology string literals ---------------------------------------------
if hits=$(grep -nE '(\.ToQueue|ListenToRabbitQueue|BindExchange|ToRabbitExchange)\(\s*"' "${PROGRAMS[@]}" 2>/dev/null) && [ -n "$hits" ]; then
    echo "INLINE TOPOLOGY LITERAL — names must come from MessagingExchanges/MessagingQueues:"
    echo "$hits" | sed 's/^/  /'
    fail=1
fi

if [ "$fail" -eq 1 ]; then
    exit 1
fi
echo "Messaging-topology audit clean."
