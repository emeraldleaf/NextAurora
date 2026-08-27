// Cloudflare Turnstile client (bot protection on order placement + the kill switch).
// The backend fails closed (403) when its Turnstile:Enabled is on and no valid
// X-Turnstile-Token header arrives; this module produces that token. With no site key
// configured (local dev) getTurnstileToken() resolves null and callers send no header —
// matching the backend's disabled state.
//
// Tokens are single-use and short-lived, so every call renders a fresh invisible widget,
// executes it, and removes it. ~300ms per call — negligible next to the POST it protects.
import { env } from '@/core/env'

interface TurnstileApi {
  render: (el: HTMLElement, opts: Record<string, unknown>) => string
  execute: (widgetId: string) => void
  remove: (widgetId: string) => void
}

declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

let scriptLoading: Promise<TurnstileApi> | null = null

function loadTurnstile(): Promise<TurnstileApi> {
  scriptLoading ??= new Promise<TurnstileApi>((resolve, reject) => {
    if (window.turnstile != null) {
      resolve(window.turnstile)
      return
    }
    const script = document.createElement('script')
    script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
    script.async = true
    script.onload = () => {
      if (window.turnstile != null) resolve(window.turnstile)
      else reject(new Error('Turnstile API missing after load'))
    }
    script.onerror = () => {
      scriptLoading = null
      reject(new Error('Failed to load Turnstile'))
    }
    document.head.appendChild(script)
  })
  return scriptLoading
}

/** Resolve a fresh single-use token, or null when Turnstile isn't configured. */
export async function getTurnstileToken(): Promise<string | null> {
  const siteKey = env.turnstileSiteKey
  if (siteKey == null || siteKey === '') return null

  const turnstile = await loadTurnstile()
  const container = document.createElement('div')
  document.body.appendChild(container)

  try {
    return await new Promise<string>((resolve, reject) => {
      const widgetId = turnstile.render(container, {
        sitekey: siteKey,
        size: 'flexible',
        execution: 'execute',
        callback: (token: string) => {
          turnstile.remove(widgetId)
          resolve(token)
        },
        'error-callback': () => {
          turnstile.remove(widgetId)
          reject(new Error('Turnstile challenge failed'))
        },
      })
      turnstile.execute(widgetId)
    })
  } finally {
    container.remove()
  }
}

/** Header name the backend's RequireTurnstile filter reads. */
export const TurnstileHeader = 'X-Turnstile-Token'
