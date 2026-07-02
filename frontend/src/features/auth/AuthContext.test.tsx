import { act, render, screen } from '@testing-library/react'
import type { User } from 'oidc-client-ts'
import { describe, expect, it, vi } from 'vitest'

import { AuthProvider } from './AuthContext'
import { useAuth } from './auth-context'

// Stub the OIDC singleton at the module boundary: no real Keycloak runs in tests. The
// handles are hoisted standalone mocks (not class-typed methods) so assertions don't trip
// @typescript-eslint/unbound-method; events is how we capture the handlers AuthProvider
// registers so a test can fire them like oidc-client-ts would.
const mocks = vi.hoisted(() => ({
  getUser: vi.fn<() => Promise<unknown>>(),
  removeUser: vi.fn(() => Promise.resolve()),
  addSilentRenewError: vi.fn<(cb: (error: Error) => void) => void>(),
}))

vi.mock('@/core/auth', () => ({
  userManager: {
    getUser: mocks.getUser,
    removeUser: mocks.removeUser,
    signinRedirect: vi.fn(),
    signoutRedirect: vi.fn(),
    events: {
      addUserLoaded: vi.fn(),
      removeUserLoaded: vi.fn(),
      addUserUnloaded: vi.fn(),
      removeUserUnloaded: vi.fn(),
      addSilentRenewError: mocks.addSilentRenewError,
      removeSilentRenewError: vi.fn(),
    },
  },
}))

const signedInUser = {
  expired: false,
  access_token: 'test-token',
  profile: { sub: 'buyer-1' },
} as unknown as User

/** Minimal consumer: renders the auth state the way the app observes it. */
function WhoAmI() {
  const { isAuthenticated, buyerId } = useAuth()
  return <p>{isAuthenticated ? `signed-in:${buyerId ?? ''}` : 'signed-out'}</p>
}

describe('AuthProvider silent-renew failure', () => {
  it('clears the session when silent renewal fails, instead of wedging as authenticated', async () => {
    // ARRANGE — a signed-in session: getUser resolves a valid (non-expired) user, so the
    // provider starts authenticated. Keycloak rotates refresh tokens (single-use), so a
    // failed renew (invalid_grant on reuse) is a REAL runtime path, not a corner case.
    mocks.getUser.mockResolvedValue(signedInUser)
    render(
      <AuthProvider>
        <WhoAmI />
      </AuthProvider>,
    )
    expect(await screen.findByText('signed-in:buyer-1')).toBeInTheDocument()

    // ACT — fire the silent-renew-error handler AuthProvider registered, exactly as
    // oidc-client-ts does when the refresh-token grant is rejected.
    const handler = mocks.addSilentRenewError.mock.calls[0]?.[0]
    expect(handler).toBeDefined()
    act(() => {
      handler?.(new Error('invalid_grant: token reuse detected'))
    })

    // ASSERT —
    // 1. The stored (now-dead) session is purged, so no stale token is replayed later.
    expect(mocks.removeUser).toHaveBeenCalled()
    // 2. The app observes signed-out: isAuthenticated flipped false, so the UI routes to
    //    login instead of holding an expired session that every API call 401s against.
    expect(await screen.findByText('signed-out')).toBeInTheDocument()
  })
})
