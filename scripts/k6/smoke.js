// k6 smoke test — exercises the public endpoints at single-VU rate to confirm the API
// stays healthy under continuous request flow. NOT a load test (yet).
//
// Run from the repo root (with AppHost running):
//   CATALOG_URL=https://localhost:XXXXX k6 run scripts/k6/smoke.js
//
// Install k6: `brew install k6` (macOS) or see https://k6.io/docs/getting-started/installation/

import http from 'k6/http';
import { check, sleep } from 'k6';

// Reads the same CATALOG_URL the bash smoke-test.sh uses, so a single .env.smoke can drive both.
const CATALOG_URL = __ENV.CATALOG_URL || 'https://localhost:7000';

export const options = {
    // Smoke profile: 1 virtual user, 30 seconds. Enough to spot startup-only bugs and
    // catch obviously-broken endpoints. Increase vus + duration for an actual load test.
    vus: 1,
    duration: '30s',
    insecureSkipTLSVerify: true,  // dev cert isn't trusted; ok for local
    thresholds: {
        // Hard fails the run if these are violated. Tune to your SLA.
        'http_req_duration': ['p(95)<500'],
        'http_req_failed':   ['rate<0.01'],
    },
};

export default function () {
    // Versioning enforcement: unversioned must 400. If this changes, the policy regressed.
    const unversioned = http.get(`${CATALOG_URL}/api/products`);
    check(unversioned, { 'unversioned returns 400': r => r.status === 400 });

    // Versioned read should 200.
    const list = http.get(`${CATALOG_URL}/api/v1/products?page=1&pageSize=10`);
    check(list, {
        'list status 200': r => r.status === 200,
        'list returns json': r => r.headers['Content-Type']?.includes('application/json'),
    });

    sleep(1);
}
