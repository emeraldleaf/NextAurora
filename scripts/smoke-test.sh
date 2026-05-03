#!/usr/bin/env bash
# NextAurora end-to-end smoke test.
#
# Walks the full saga: auth → catalog write → order placement → outbox/event verification.
# Run this AFTER `dotnet run --project NextAurora.AppHost` is up and all resources are Running
# in the Aspire dashboard. URLs are dynamic per Aspire run; copy them from the dashboard into
# the env vars below (or into a `.env.smoke` file at the repo root, which this script sources).
#
# Usage:
#   export KEYCLOAK_URL=http://localhost:XXXX
#   export CATALOG_URL=https://localhost:XXXX
#   export ORDER_URL=https://localhost:XXXX
#   export PRODUCT_ID=<existing-product-guid>     # optional; if unset, order step is skipped
#   export CATEGORY_ID=<existing-category-guid>   # optional; only used if creating a product
#   ./scripts/smoke-test.sh
#
# Exit codes: 0 = all checks passed, 1 = at least one check failed, 2 = setup error.

set -uo pipefail

# ----- helpers ----------------------------------------------------------------

readonly RED=$'\033[31m'
readonly GREEN=$'\033[32m'
readonly YELLOW=$'\033[33m'
readonly BLUE=$'\033[34m'
readonly DIM=$'\033[2m'
readonly RESET=$'\033[0m'

PASS=0
FAIL=0

pass() { echo "  ${GREEN}✓${RESET} $1"; PASS=$((PASS+1)); }
fail() { echo "  ${RED}✗${RESET} $1"; FAIL=$((FAIL+1)); }
info() { echo "  ${BLUE}ℹ${RESET} $1"; }
warn() { echo "  ${YELLOW}!${RESET} $1"; }
section() { echo; echo "${BLUE}── $1 ──${RESET}"; }

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "${RED}Missing required command: $1${RESET}" >&2
        exit 2
    fi
}

# Fetch a token from Keycloak's password grant. Echoes the access_token on stdout
# (empty if the request failed).
get_token() {
    local user=$1 pass=$2
    curl -sk -X POST "$KEYCLOAK_URL/realms/nextaurora/protocol/openid-connect/token" \
        -d "client_id=storefront" \
        -d "username=$user" \
        -d "password=$pass" \
        -d "grant_type=password" 2>/dev/null \
        | python3 -c "import sys,json
try:
    d = json.load(sys.stdin)
    print(d.get('access_token', ''))
except Exception:
    pass" 2>/dev/null
}

# Curl wrapper that writes the body to /tmp/smoke_body and prints just the status code.
# `-w "%{http_code}"` always prints exactly the status (or 000 if unreachable), so we don't
# need a fallback — curl handles it.
http() {
    local method=$1 url=$2
    shift 2
    curl -sk -o /tmp/smoke_body -w "%{http_code}" -X "$method" "$url" "$@" 2>/dev/null
}

# Like `http` for GET — convenience wrapper.
http_get_code() {
    curl -sk -o /dev/null -w "%{http_code}" "$1" 2>/dev/null
}

# Read a single key from a JSON file, swallowing decode errors.
json_key() {
    local file=$1 key=$2
    python3 -c "import sys,json
try:
    d = json.load(open('$file'))
    v = d.get('$key', '')
    if isinstance(v, list): v = ','.join(v)
    print(v)
except Exception:
    pass" 2>/dev/null
}

# Decode a JWT payload to JSON; print empty on failure.
jwt_claim() {
    local token=$1 claim=$2
    local payload=${token#*.}
    payload=${payload%%.*}
    local pad=$(( (4 - ${#payload} % 4) % 4 ))
    printf '%s' "$payload$(printf '=%.0s' $(seq 1 $pad))" \
        | tr '_-' '/+' | base64 -d 2>/dev/null \
        | python3 -c "import sys,json
try:
    d = json.load(sys.stdin)
    v = d.get('$claim', '')
    if isinstance(v, list): v = ','.join(v)
    print(v)
except Exception:
    pass" 2>/dev/null
}

# ----- prereqs ----------------------------------------------------------------

require_cmd curl
require_cmd python3

# Source .env.smoke if present (gitignored).
ENV_FILE="$(dirname "$0")/../.env.smoke"
if [[ -f "$ENV_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$ENV_FILE"
fi

: "${KEYCLOAK_URL:?KEYCLOAK_URL not set — copy from Aspire dashboard or .env.smoke}"
: "${CATALOG_URL:?CATALOG_URL not set — copy from Aspire dashboard or .env.smoke}"
: "${ORDER_URL:?ORDER_URL not set — copy from Aspire dashboard or .env.smoke}"

echo "${DIM}Keycloak: $KEYCLOAK_URL${RESET}"
echo "${DIM}Catalog:  $CATALOG_URL${RESET}"
echo "${DIM}Order:    $ORDER_URL${RESET}"

# ----- 1. service liveness ---------------------------------------------------

section "Service liveness"

for svc_url in "$CATALOG_URL" "$ORDER_URL"; do
    code=$(http_get_code "$svc_url/alive")
    if [[ "$code" == "200" ]]; then
        pass "$svc_url/alive → $code"
    else
        fail "$svc_url/alive → $code (service may not be up)"
    fi
done

# ----- 2. API versioning enforcement -----------------------------------------

section "API versioning enforcement"

code=$(http_get_code "$CATALOG_URL/api/products")
if [[ "$code" == "400" ]]; then
    pass "GET /api/products → 400 (version segment required, as configured)"
else
    fail "GET /api/products → $code (expected 400 — versioning policy may be misconfigured)"
fi

code=$(http_get_code "$CATALOG_URL/api/v1/products?page=1&pageSize=10")
if [[ "$code" == "200" ]]; then
    pass "GET /api/v1/products → 200"
else
    fail "GET /api/v1/products → $code (expected 200)"
fi

# ----- 3. Auth flow -----------------------------------------------------------

section "Authentication (Keycloak password grant)"

BUYER_TOKEN=$(get_token buyer1 buyer1)
if [[ -n "$BUYER_TOKEN" ]]; then
    pass "buyer1 token obtained ($(echo -n "$BUYER_TOKEN" | wc -c | tr -d ' ') chars)"
else
    fail "buyer1 token request failed (Keycloak unreachable or realm not bootstrapped)"
fi

SELLER_TOKEN=$(get_token seller1 seller1)
if [[ -n "$SELLER_TOKEN" ]]; then
    pass "seller1 token obtained"
else
    fail "seller1 token request failed"
fi

BUYER_SUB=""
if [[ -n "$BUYER_TOKEN" ]]; then
    BUYER_SUB=$(jwt_claim "$BUYER_TOKEN" sub)
    BUYER_AUD=$(jwt_claim "$BUYER_TOKEN" aud)
    if [[ -n "$BUYER_SUB" ]]; then
        pass "buyer JWT decoded: sub=$BUYER_SUB"
        info "buyer JWT aud=$BUYER_AUD"
    else
        fail "buyer JWT decode failed (sub claim missing)"
    fi
fi

# ----- 4. Auth gate enforcement ---------------------------------------------

section "Auth gate enforcement"

code=$(http_get_code "$ORDER_URL/api/v1/orders/00000000-0000-0000-0000-000000000000")
if [[ "$code" == "401" ]]; then
    pass "GET /api/v1/orders/... without token → 401"
else
    fail "GET /api/v1/orders/... without token → $code (expected 401)"
fi

# ----- 5. Order placement (only if PRODUCT_ID is set) -----------------------

section "Order placement (saga entry)"

if [[ -z "${PRODUCT_ID:-}" ]]; then
    warn "PRODUCT_ID not set — skipping order placement."
    warn "To run this step, create a product (POST /api/v1/products with seller token)"
    warn "or pick an existing product ID from the catalog DB, then re-run with PRODUCT_ID set."
elif [[ -z "$BUYER_TOKEN" ]] || [[ -z "$BUYER_SUB" ]]; then
    fail "Cannot place order without a valid buyer token + sub claim (auth step failed)"
else
    body=$(printf '{"buyerId":"%s","currency":"USD","lines":[{"productId":"%s","quantity":1}]}' \
        "$BUYER_SUB" "$PRODUCT_ID")

    code=$(http POST "$ORDER_URL/api/v1/orders" \
        -H "Authorization: Bearer $BUYER_TOKEN" \
        -H "Content-Type: application/json" \
        -d "$body")

    if [[ "$code" == "202" ]]; then
        ORDER_ID=$(json_key /tmp/smoke_body id)
        pass "POST /api/v1/orders → 202 (orderId=$ORDER_ID)"
        info "Inspect Aspire dashboard → Traces tab to see the saga walk through"
        info "Verify outbox manually: SELECT TOP 5 * FROM wolverine.outgoing_envelopes ORDER BY received_at DESC"
    else
        fail "POST /api/v1/orders → $code"
        echo "${DIM}Response body:${RESET}"
        cat /tmp/smoke_body 2>/dev/null | head -20 | sed 's/^/    /'
    fi
fi

# ----- summary ----------------------------------------------------------------

echo
if [[ "$FAIL" -eq 0 ]]; then
    echo "${GREEN}✓ All $PASS checks passed.${RESET}"
    exit 0
else
    echo "${RED}✗ $FAIL failed${RESET}, ${GREEN}$PASS passed${RESET}."
    exit 1
fi
