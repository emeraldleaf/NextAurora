# NextAurora Storefront — Frontend Plan

> **What this is.** The architecture decision + phased plan for the portfolio frontend: a React
> SPA that drives the full order saga through a real UI, *narrates what the backend is doing at
> each step*, and exposes the observability layer (correlation IDs, saga progression, traces).
> The frontend is itself a portfolio artifact — it must demonstrate the same engineering rigor
> as the backend: encoded canon, enforced rules, tests that prove behavior.

**Status:** built and deployed — phases 0–4 shipped; the SPA is containerized
(`Dockerfile.frontend`, nginx) and live at https://shop.emeraldleaf.dev. Canon lives in [frontend/CLAUDE.md](../frontend/CLAUDE.md) (written before the scaffold, same encode-first method as the backend).

---

## The pitch (what makes this portfolio-worthy)

Most demo frontends are CRUD skins. This one has two distinguishing features:

1. **The saga narrator.** Placing an order walks the user through the choreography saga *as it
   happens*: Order placed (outbox → RabbitMQ) → Payment processed → Shipment dispatched → Notification
   sent. Each step shows a "behind the scenes" panel explaining the mechanism (transactional
   outbox, idempotent consumers, batch gRPC validation, optimistic concurrency) with links into
   the repo. The app teaches its own architecture — the same teaching-grade ethos as the
   backend's tier-1 comments.

2. **Exposed observability.** The UI surfaces what's normally backstage: the `X-Correlation-Id`
   from response headers, live order-status polling visualized as a saga timeline, and (local
   dev) deep links into the Aspire dashboard trace for the exact correlation ID. TanStack Query
   devtools stay enabled in the demo build — the query cache IS part of the show.

## Stack decision

| Concern | Choice | Why (and why not the alternative) |
|---|---|---|
| Build/runtime | **Vite + React 19 + TypeScript (SPA, CSR)** | The backend is already 5 REST/gRPC services — there is no server to render from and no SEO need. Next.js would add an RSC/SSR layer with nothing to do; CSR + static hosting is the honest architecture. (The Vercel skill's `server-*` rules therefore don't apply; everything else does.) |
| Server state | **TanStack Query v5** | The "never fetch in useEffect" rule needs a home. Query gives caching, request dedup, retries, and devtools. Mirrors the backend's HybridCache story: cache + explicit invalidation in the mutation that wrote. |
| Routing | **TanStack Router** | Type-safe routes + loader-based prefetch pairs with Query; route-level code splitting built in. React Router v7 is the acceptable fallback if friction appears. |
| Client state | **useState/useReducer locally; one small Zustand store** for session/UI globals (auth, theme) | No Redux — server state lives in Query; what's left is small. |
| Auth | **oidc-client-ts against the existing Keycloak** (auth-code + PKCE) | Backend JWTs already come from Keycloak; the SPA just becomes a real OIDC client. No password grant in the browser. |
| UI | **Tailwind CSS v4 + shadcn/ui** | Fast to build well; components are copied in (no runtime dep), so bundle stays analyzable. |
| Testing | **Vitest + React Testing Library + MSW** (unit/component), **Playwright** (E2E against the Aspire stack) | MSW mocks the five services' REST APIs at the network boundary — tests assert user-visible behavior, not implementation. Playwright runs the real saga walk-through: the E2E test IS the demo script. |
| Lint/enforce | **ESLint 9 flat config**: `eslint-plugin-react-hooks` (with React Compiler rule), `eslint-plugin-import` `no-restricted-paths` for feature boundaries, `typescript-eslint` strict | The canon is enforced mechanically, not by convention — same enforcement-spectrum thinking as the backend (convention → lint rule → CI). |
| Compiler | **React Compiler** (babel plugin, React 19) | Auto-memoization removes most manual `useMemo`/`memo` ceremony; the canon's re-render rules become "don't fight the compiler" plus the structural cases it can't fix. |

## Architecture: feature folders, mirroring the backend's VSA

```
frontend/
  src/
    app/            # Router root, providers, layout shells (thin — no business logic)
    features/       # One folder per business capability — the VSA mirror
      catalog/      #   browse/search products (CatalogService)
      ordering/     #   cart + checkout + place order (OrderService)
      orders/       #   my orders, order detail + saga timeline (Order/Shipping)
      saga-narrator/#   the "behind the scenes" panel + step explanations
      observability/#   correlation-ID surfacing, trace links, status polling
      auth/         #   Keycloak OIDC session
    shared/         # Domain-agnostic UI primitives, hooks, api client base
    core/           # Singletons: query client, router, auth client, env config
  CLAUDE.md         # The React canon (always-on rules for this folder)
```

Feature-boundary rules (enforced by ESLint `no-restricted-paths`): features import from
`shared/` and `core/`, never from each other's internals — only via a feature's `index.ts`
public API. `shared/` never imports from features. Code moves to `shared/` after proven reuse
(2–3 features), not speculatively — the same "interfaces earn their keep" discipline as the
backend. The symmetry is the portfolio story: **vertical slices on both sides of the wire.**

## Performance budget (CI-checked, not aspirational)

- Initial JS ≤ 200 KB gzipped; route chunks lazy-loaded (`bundle-dynamic-imports`)
- Lighthouse performance ≥ 95 on the catalog page (CI run against preview build)
- No request waterfalls on the critical path: route loaders kick off queries; `Promise.all`
  for independent fetches; Suspense boundaries stream what's ready (`async-*` rules)
- Product list virtualized past 50 items (`list virtualization`)
- Bundle analyzed in CI (`rollup-plugin-visualizer` report attached to PR artifacts)

## Backend touchpoints (exists today / needs adding)

| Frontend need | Backend state |
|---|---|
| Product browse/search | ✅ `GET /api/v1/products`, `/products/search` (rate-limited) |
| Place order | ✅ `POST /api/v1/orders` (202-style: returns ID, saga proceeds async) |
| Order status / saga timeline | ✅ `GET /api/v1/orders/{id}` (poll); ⚠️ consider an SSE endpoint later — **don't build until polling proves insufficient** |
| Shipment by order | ✅ `GET /api/v1/shipments/order/{orderId}` |
| Correlation ID on responses | ✅ `CorrelationIdMiddleware`; `AddFrontendCors` exposes `X-Correlation-Id` ([Extensions.cs](../NextAurora.ServiceDefaults/Extensions.cs)) |
| CORS for the SPA origin | ✅ `AddFrontendCors` in ServiceDefaults — origins injected via `Frontend__AllowedOrigins` (AppHost + compose) |
| Keycloak SPA client | ✅ `storefront` public client (auth-code + PKCE) in `nextaurora-realm.json` |

## Phases (each = one PR, demoable increment)

1. **Phase 0 — Scaffold + canon enforcement.** Vite app under `frontend/`, ESLint flat config
   with boundary rules, Vitest + RTL + MSW wired, Playwright smoke, CI job (lint, typecheck,
   test, build, bundle-size check). Proof: CI green on a hello-world feature slice.
2. **Phase 1 — Catalog browse.** Product list + search against CatalogService (CORS + env
   config). Query cache + virtualized list + route code-splitting demonstrate the perf canon.
3. **Phase 2 — Auth + checkout.** Keycloak OIDC, cart (local state), place order. The IDOR
   story from the buyer's seat: you only ever see your own orders.
4. **Phase 3 — Saga timeline + narrator.** Order detail polls status; timeline renders
   Placed → Paid → Shipped with per-step "what just happened in the backend" panels.
5. **Phase 4 — Observability surface.** Correlation-ID chip on every mutation, Aspire trace
   deep-links (dev), query-cache devtools panel, failure-path demo (payment failure → saga
   compensation visible in UI).
6. **Phase 5 — Deploy + polish.** As built: the SPA ships as an nginx image
   (`Dockerfile.frontend`) on the Hetzner box behind Caddy at shop.emeraldleaf.dev — not
   static hosting. Still open: Lighthouse CI gate and bundle visualizer in CI.

## Sources the canon distills

- [react.dev — You Might Not Need an Effect](https://react.dev/learn/you-might-not-need-an-effect) (the effects discipline, wholesale)
- [Vercel Engineering — react-best-practices skill](https://github.com/vercel-labs/agent-skills/tree/main/skills/react-best-practices) (70 rules; vendored at `.claude/skills/vercel-react-best-practices/`)
- [patterns.dev](https://patterns.dev/) (rendering/perf patterns; render-props & HOCs noted as superseded by hooks)
- [overreacted.io](https://overreacted.io/) (mental models: A Complete Guide to useEffect, Before You memo(), React as a UI Runtime, Writing Resilient Components)
- [Feature-based architecture guide](https://medium.com/digigeek/from-chaos-to-cohesion-a-feature-based-guide-for-react-and-nextjs-33134c0dede9) (folder structure + boundary enforcement)
