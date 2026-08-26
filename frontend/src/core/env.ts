// Public runtime config (core/ = singletons). Vite inlines import.meta.env at build time;
// only VITE_-prefixed keys reach the bundle, and nothing here is secret by rule — see
// frontend/CLAUDE.md "Security". Aspire assigns dynamic service ports per run, so dev
// copies the Catalog URL from the dashboard into frontend/.env.local (same convention as
// the repo's .env.smoke).
export const env = {
  catalogApiUrl: (import.meta.env.VITE_CATALOG_API_URL as string | undefined) ?? 'http://localhost:5301',
  orderApiUrl: (import.meta.env.VITE_ORDER_API_URL as string | undefined) ?? 'http://localhost:5302',
  paymentApiUrl: (import.meta.env.VITE_PAYMENT_API_URL as string | undefined) ?? 'http://localhost:5303',
  keycloakUrl: (import.meta.env.VITE_KEYCLOAK_URL as string | undefined) ?? 'http://localhost:8080',
  keycloakRealm: (import.meta.env.VITE_KEYCLOAK_REALM as string | undefined) ?? 'nextaurora',
  keycloakClientId: (import.meta.env.VITE_KEYCLOAK_CLIENT_ID as string | undefined) ?? 'storefront',
}
