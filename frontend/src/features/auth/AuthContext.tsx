import type { User } from 'oidc-client-ts'
import { useEffect, useState, type ReactNode } from 'react'

import { userManager } from '@/core/auth'

import { AuthContext, type AuthState } from './auth-context'

/**
 * Wraps the app and tracks the OIDC session. Subscribes to UserManager events
 * (the external auth store) rather than polling — the one legitimate effect here is
 * synchronizing React with a non-React system, which is exactly what effects are for
 * (frontend/CLAUDE.md "Effects discipline"). The buyer's id is the JWT `sub`, which the
 * backend matches against `command.BuyerId` and the IDOR predicates — it's the single
 * identity the whole order flow keys on.
 */
export function AuthProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [user, setUser] = useState<User | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  useEffect(() => {
    userManager
      .getUser()
      .then(setUser)
      .catch(() => {
        setUser(null)
      })
      .finally(() => {
        setIsLoading(false)
      })

    const onLoaded = (u: User) => {
      setUser(u)
    }
    const onUnloaded = () => {
      setUser(null)
    }
    // Keycloak rotates refresh tokens (revokeRefreshToken + refreshTokenMaxReuse: 0 in the
    // realm), so any refresh-token reuse — a network retry mid-renew, a tab restored from
    // bfcache — returns invalid_grant and kills the grant. Without this handler a failed
    // silent renew leaves the app holding an expired session that every API call 401s
    // against. Clearing the stored user flips isAuthenticated → false so the UI shows the
    // login path instead of wedging.
    const onSilentRenewError = (error: Error) => {
      // Surface the cause (invalid_grant vs network vs IdP down) — a silent sign-out with
      // no trace is undiagnosable in the field.
      console.warn('Silent token renewal failed — clearing session', error)
      void userManager.removeUser()
      setUser(null)
    }
    userManager.events.addUserLoaded(onLoaded)
    userManager.events.addUserUnloaded(onUnloaded)
    userManager.events.addSilentRenewError(onSilentRenewError)
    return () => {
      userManager.events.removeUserLoaded(onLoaded)
      userManager.events.removeUserUnloaded(onUnloaded)
      userManager.events.removeSilentRenewError(onSilentRenewError)
    }
  }, [])

  const value: AuthState = {
    user,
    isAuthenticated: user != null && !user.expired,
    isLoading,
    buyerId: user?.profile.sub ?? null,
    login: () => userManager.signinRedirect(),
    logout: () => userManager.signoutRedirect(),
  }

  return <AuthContext value={value}>{children}</AuthContext>
}
