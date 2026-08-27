// The kill-switch client (#208): PaymentService's DemoMode-gated listener controls. The
// status query 404s when the backend runs without DemoMode — the UI treats that as "no
// kill switch in this deployment" and renders nothing (retry is off for that reason).
import { queryOptions } from '@tanstack/react-query'

import { userManager } from '@/core/auth'
import { env } from '@/core/env'
import { ApiError, getJsonAuthed, postJsonAuthed } from '@/shared/api/http'
import { TurnstileHeader, getTurnstileToken } from '@/shared/turnstile'

export interface ListenerStatus {
  /** Wolverine ListeningStatus name ('Accepting' when healthy) or 'Unavailable'. */
  status: string
  autoReviveSeconds: number
}

async function token(): Promise<string> {
  const user = await userManager.getUser()
  if (user == null || user.expired) throw new Error('Not authenticated')
  return user.access_token
}

export const demoKeys = {
  listener: ['orders', 'demo-listener'] as const,
}

/** Poll fast enough that Wolverine's own auto-revive shows up without a click. */
export const LISTENER_POLL_INTERVAL_MS = 3000

export function listenerStatusQuery() {
  return queryOptions({
    queryKey: demoKeys.listener,
    queryFn: async ({ signal }) => {
      const { data } = await getJsonAuthed<ListenerStatus>(env.paymentApiUrl, '/api/v1/demo/listener', await token(), signal)
      return data
    },
    refetchInterval: LISTENER_POLL_INTERVAL_MS,
    // 404 = DemoMode off; retrying would never change the answer.
    retry: (failureCount, error) => !(error instanceof ApiError && error.status === 404) && failureCount < 2,
  })
}

async function turnstileHeaders(): Promise<Record<string, string> | undefined> {
  const t = await getTurnstileToken()
  return t == null ? undefined : { [TurnstileHeader]: t }
}

export async function pauseListener(): Promise<ListenerStatus> {
  const { data } = await postJsonAuthed<ListenerStatus>(env.paymentApiUrl, '/api/v1/demo/listener/pause', {}, await token(), undefined, await turnstileHeaders())
  return data
}

export async function resumeListener(): Promise<ListenerStatus> {
  const { data } = await postJsonAuthed<ListenerStatus>(env.paymentApiUrl, '/api/v1/demo/listener/resume', {}, await token(), undefined, await turnstileHeaders())
  return data
}
