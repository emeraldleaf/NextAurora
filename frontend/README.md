# NextAurora Storefront (React SPA)

The portfolio frontend — Vite + React 19 + TanStack Query/Router + Tailwind. Architecture
decision + phased plan: [docs/frontend-plan.md](../docs/frontend-plan.md). Always-on rules:
[CLAUDE.md](CLAUDE.md) (this folder's canon).

## Run against the local backend

1. Start the backend: `dotnet run --project NextAurora.AppHost` (Docker running). Wait for
   the Aspire dashboard and `catalog-service` to reach Running.
2. Copy the Catalog URL from the dashboard's Resources tab:
   `cp .env.example .env.local` and set `VITE_CATALOG_API_URL` (ports are dynamic per
   Aspire run — same convention as the repo's `.env.smoke`).
3. `npm install && npm run dev` → http://localhost:5173 (the origin the backend's CORS
   policy allows — see ServiceDefaults `AddFrontendCors`).

## Checks

```bash
npm run lint        # ESLint incl. feature-boundary rules
npm run typecheck
npm test            # Vitest + RTL + MSW
npm run build       # bundle budget: initial JS ≤ 200 KB gz (currently ~104 KB)
```
