import type { User } from 'oidc-client-ts'
import { createContext, useContext } from 'react'

export interface AuthState {
  user: User | null
  isAuthenticated: boolean
  isLoading: boolean
  buyerId: string | null
  login: () => Promise<void>
  logout: () => Promise<void>
}

// Context + hook split into their own module so AuthContext.tsx exports only the component
// (keeps React Fast Refresh happy — frontend/CLAUDE.md defers to the canon, and a
// zero-warning build is the bar).
export const AuthContext = createContext<AuthState | null>(null)

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (ctx == null) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
