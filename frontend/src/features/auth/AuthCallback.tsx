import { useNavigate } from '@tanstack/react-router'
import { useEffect, useState } from 'react'

import { userManager } from '@/core/auth'

/**
 * Lands the OIDC redirect: exchanges the auth code (+ PKCE verifier) for tokens, then
 * routes home. Completing the code exchange is synchronizing with an external system —
 * a legitimate effect. The redirect-away is done via the router after success, not in the
 * effect's render path.
 */
export function AuthCallback() {
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    userManager
      .signinRedirectCallback()
      .then(() => navigate({ to: '/' }))
      .catch((e: unknown) => {
        setError(e instanceof Error ? e.message : 'Sign-in failed')
      })
  }, [navigate])

  return (
    <div className="mx-auto max-w-md p-8 text-center">
      {error == null ? (
        <p role="status" className="text-zinc-500">
          Signing you in…
        </p>
      ) : (
        <p role="alert" className="text-red-600">
          Sign-in failed: {error}
        </p>
      )}
    </div>
  )
}
