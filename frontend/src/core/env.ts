// Public runtime config (core/ = singletons). Vite inlines import.meta.env at build time;
// only VITE_-prefixed keys reach the bundle, and nothing here is secret by rule — see
// frontend/CLAUDE.md "Security". Aspire assigns dynamic service ports per run, so dev
// copies the Catalog URL from the dashboard into frontend/.env.local (same convention as
// the repo's .env.smoke).
export const env = {
  catalogApiUrl: (import.meta.env.VITE_CATALOG_API_URL as string | undefined) ?? 'http://localhost:5301',
}
