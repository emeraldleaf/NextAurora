# NextAurora Storefront — React Canon

> Always-on rules for everything under `frontend/`. Same contract as the repo-root CLAUDE.md:
> lean headlines + the one-line why; deep dives live in the vendored
> [`.claude/skills/vercel-react-best-practices`](../.claude/skills/vercel-react-best-practices/SKILL.md)
> skill and [docs/frontend-plan.md](../docs/frontend-plan.md). **This canon takes precedence
> over the vendored skill where they disagree** — the skill is reference, the canon is law
> (same hierarchy as root CLAUDE.md over the dotnet-performance skill). The Continuous Rule
> Encoding loop applies here identically — frontend findings get encoded the session they're
> found.

## Stack (decided — don't relitigate per-PR)

Vite + React 19 + TypeScript (strict), CSR SPA. TanStack Query v5 (server state), TanStack
Router, Zustand (small session/UI globals only), Tailwind v4 (shadcn/ui chosen, not yet
adopted — no component copied in yet), oidc-client-ts → Keycloak (auth-code + PKCE).
React Compiler enabled. Rationale: [docs/frontend-plan.md](../docs/frontend-plan.md).

## Architecture rules

- **Feature folders mirror the backend's VSA.** `src/features/<capability>/` owns its
  components, hooks, api calls, and types. `src/shared/` is domain-agnostic; `src/core/` is
  singletons (query client, router, auth). `src/app/` is thin shell — no business logic.
- **Feature boundaries are the rule; enforcement is partial today.** Features never import from another
  feature's internals — only via its `index.ts` public API. `shared/` never imports from
  features. ESLint `import/no-restricted-paths` makes violations build errors — but today
  its zones only protect `catalog`'s internals; the other features are still convention
  (`OrderList.test.tsx` already imports `@/features/auth/auth-context` directly).
  Generalizing the zones to every feature is open work.
- **Barrel files are intentional.** A feature's `index.ts` exports its public surface
  explicitly. Everywhere else, import directly — wildcard re-export barrels defeat
  tree-shaking and bloat chunks (`bundle-barrel-imports`).
- **Promotion to `shared/` requires proven reuse** (2–3 features independently need it).
  Speculative abstraction is the same dead weight here as speculative interfaces are in the
  backend.

## Server state (the biggest rule)

- **All server data flows through TanStack Query. Fetching in `useEffect` is banned.**
  Hand-rolled effect-fetching reimplements (badly) what Query provides: dedup, caching,
  race-condition handling, retries. The react.dev effects guide itself says "use a library."
- **Query keys are a typed convention**: `[feature, entity, params]` — e.g.
  `['catalog', 'products', { search }]`. Key factories live in the feature's `api/` module.
- **Mutations invalidate (or update) their affected queries in the same mutation definition**
  (`onSuccess`) — never "later" or via refetch-on-focus luck. This is the backend's
  "cache invalidation in the write path" rule, client-side.
- **Server data is not copied into `useState`.** Render from the query result; derive during
  render. Copying creates a second source of truth that goes stale.

## Effects discipline (distilled from react.dev "You Might Not Need an Effect")

- **An Effect is only for synchronizing with an external system** (browser API, non-React
  widget, subscription). Everything else has a better tool:
  - Derived data → **calculate during render** (or `useMemo` if measured-expensive).
  - Reset state on prop change → **`key` prop**, not an effect.
  - "User did X" logic (POST, notification, navigation) → **event handler**, never an effect
    watching a flag.
  - Subscribing to an external store → **`useSyncExternalStore`**, not addEventListener-in-effect.
  - State chains (effect sets state → triggers effect) → compute it all in the event handler.
  - Notifying/passing data to parents → call the callback in the handler; or lift the state up.
- **Effect dependencies are facts, not knobs.** Never lie to the dependency array to control
  *when* an effect runs — restructure instead (move logic to handlers, split effects with
  independent deps). `eslint-plugin-react-hooks` `exhaustive-deps` is an error, not a warning.

## Render & bundle performance

- **Don't fight the React Compiler.** No reflexive `useMemo`/`useCallback`/`memo` — the
  compiler memoizes. Manual memoization needs a profiler trace justifying it (the backend's
  "measure before optimizing" rule). The structural cases the compiler can't fix are still
  yours: don't define components inside components, hoist static JSX/default non-primitive
  props, use functional `setState` for stable callbacks, refs for transient high-frequency
  values.
- **No request waterfalls on the critical path.** Route loaders start queries before render;
  independent fetches go in parallel (`Promise.all` / parallel `useQuery`s); Suspense
  boundaries stream what's ready. A fetch that waits on a render that waits on a fetch is the
  client-side N+1.
- **Route-level code splitting is the default**; heavy below-the-fold components load via
  dynamic import. Third-party scripts (analytics) load after hydration. Initial bundle budget:
  ≤ 200 KB gz — not yet enforced in CI (the frontend job runs lint, typecheck, test, build
  only; no size gate exists yet).
- **Virtualize lists past ~50 items** (product grids, order history).
- **`startTransition`/`useDeferredValue` for non-urgent updates** (search-as-you-type filters)
  — keep input latency flat.

## Security

- **Auth flow: authorization code + PKCE, full stop.** The OAuth Browser-Based Apps BCP makes
  PKCE a MUST for SPA public clients and formally deprecates the implicit flow — no
  `response_type=token` anywhere, ever.
- **Tokens live in the oidc-client-ts session (`sessionStorage`), never `localStorage`** — an
  XSS payload can read either store — and can exfiltrate a token while the tab lives, which
  `sessionStorage` does nothing to stop; it only limits browser-side persistence (per-tab,
  gone on restart). Short token lifetimes + silent renew shrink the abuse window. **Known, documented
  trade-off:** the BCP ranks browser-held tokens as the *least* secure of its three patterns
  and strongly recommends a BFF (tokens server-side, browser gets only an HttpOnly cookie) for
  sensitive/business apps. For this demo storefront (fake data, no real PII/payments) the
  browser-client pattern is acceptable; if this ever fronts real user data, the BFF becomes the
  required shape — same "documented trade-off at the call site" discipline as the backend's
  leading-wildcard search.
- **Refresh tokens: rely on Keycloak's rotation.** The BCP requires rotation (or
  sender-constraining) + bounded lifetime for SPA refresh tokens — enable refresh-token
  rotation on the SPA client in realm config and keep refresh lifetime tied to the SSO
  session.
- **The client never computes or trusts money/authorization fields** — display what the server
  returns. (Backend canon: server-controlled fields. The cart's displayed total is a preview;
  the authoritative total comes back from `POST /orders`.)
- **No secrets in the bundle.** `import.meta.env` carries public config only; anything secret
  belongs behind an endpoint.

## Testing

- **Test user-visible behavior, not implementation.** RTL queries by role/label; assert what
  the user sees. No testing of hook internals or state shapes.
- **MSW mocks at the network boundary** — component/integration tests run against mocked REST
  handlers per service, mirroring real response shapes (including error and slow cases).
- **Every feature ships with: happy path + error path + loading/empty state tests.** The
  backend's "coverage for the contract" rule.
- **Playwright E2E for the saga walk-through** (against the real Aspire stack) is the planned
  regression gate and demo script — not yet built: no `@playwright/test` dependency or e2e
  suite exists. See [docs/frontend-plan.md](../docs/frontend-plan.md).

## Conventions

- Components `PascalCase.tsx`, hooks `useX.ts`, feature folders `kebab-case`, one component
  per file. Named exports (default exports only where the router requires them).
- TypeScript strict; no `any` (`unknown` + narrowing); API response types live in the
  feature's `types/` and are derived from backend DTO shapes.
- Comments follow the repo's two-tier convention: teaching-grade where a pattern is
  demonstrated (the saga-narrator and observability features ARE the documentation), silent
  plumbing elsewhere.
