# k6 load tests

Endpoint-level performance checks. The bash `scripts/smoke-test.sh` covers correctness; these scripts cover behavior under sustained request flow.

## Install k6

macOS: `brew install k6`. Linux/Windows: see [k6.io/docs/getting-started/installation](https://k6.io/docs/getting-started/installation/).

## Run the smoke test

```bash
# AppHost must be running. CATALOG_URL is shared with scripts/smoke-test.sh —
# either source .env.smoke or export it inline:
CATALOG_URL=https://localhost:XXXXX k6 run scripts/k6/smoke.js
```

Output ends with a checks summary and threshold pass/fail. Non-zero exit code if any threshold fires.

## Files

- **`smoke.js`** — 1 VU for 30s. Hits `GET /api/products` (expects 400) and `GET /api/v1/products` (expects 200). Confirms versioning policy and basic read path stay healthy. Thresholds: p95 < 500ms, error rate < 1%.

## What's not here yet

- Auth-required endpoints (need a Keycloak token grant in the script).
- Order placement / saga load. That requires seeded products and a buyer JWT.
- Multi-VU / ramp-up profiles.

These are reasonable next additions when there's a specific load scenario to model.
