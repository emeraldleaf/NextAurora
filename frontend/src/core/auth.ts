import { UserManager, WebStorageStateStore } from 'oidc-client-ts'

import { env } from './env'

// OIDC against the existing Keycloak realm (core/ = singletons). Authorization-code + PKCE
// only — the OAuth Browser-Based Apps BCP makes PKCE a MUST for SPA public clients and
// deprecates the implicit flow (see frontend/CLAUDE.md "Security"). Tokens live in the
// oidc-client-ts session (sessionStorage — the library's session mechanism, explicitly NOT
// localStorage, which the canon forbids: an XSS that reads localStorage steals tokens for
// every tab, forever).
export const userManager = new UserManager({
  authority: `${env.keycloakUrl}/realms/${env.keycloakRealm}`,
  client_id: env.keycloakClientId,
  redirect_uri: `${window.location.origin}/auth/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  // PKCE is on by default for code flow in oidc-client-ts; stated explicitly for auditability.
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  // Drop the `code`/`state` query params off the URL after the callback completes.
  automaticSilentRenew: true,
})
