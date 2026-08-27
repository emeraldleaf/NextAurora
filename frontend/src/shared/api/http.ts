// Thin fetch wrapper for the per-service REST APIs. Deliberately small: TanStack Query owns
// caching/retries/dedup; this owns base-URL resolution, JSON, error shaping, and surfacing
// the X-Correlation-Id the backend stamps on every response (exposed via CORS specifically
// so the observability UI can show it — see ServiceDefaults AddFrontendCors).

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly correlationId: string | null,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

export interface ApiResponse<T> {
  data: T
  correlationId: string | null
}

async function request<T>(baseUrl: string, path: string, init: RequestInit): Promise<ApiResponse<T>> {
  const response = await fetch(`${baseUrl}${path}`, init)
  const correlationId = response.headers.get('X-Correlation-Id')

  if (!response.ok) {
    // RFC 7807 ProblemDetails body — generic detail only by backend contract; the
    // correlation ID is the support handle, so it rides the error.
    throw new ApiError(`Request failed with status ${String(response.status)}`, response.status, correlationId)
  }

  // 202/204 carry no body; guard the parse so order placement (202 Accepted) doesn't throw.
  const text = await response.text()
  return { data: (text ? JSON.parse(text) : null) as T, correlationId }
}

function authHeader(accessToken?: string): Record<string, string> {
  return accessToken == null ? {} : { Authorization: `Bearer ${accessToken}` }
}

export function getJson<T>(baseUrl: string, path: string, signal?: AbortSignal): Promise<ApiResponse<T>> {
  return request<T>(baseUrl, path, { signal })
}

export function getJsonAuthed<T>(baseUrl: string, path: string, accessToken: string, signal?: AbortSignal): Promise<ApiResponse<T>> {
  return request<T>(baseUrl, path, { signal, headers: authHeader(accessToken) })
}

export function postJsonAuthed<T>(
  baseUrl: string,
  path: string,
  body: unknown,
  accessToken: string,
  signal?: AbortSignal,
  extraHeaders?: Record<string, string>,
): Promise<ApiResponse<T>> {
  return request<T>(baseUrl, path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeader(accessToken), ...extraHeaders },
    body: JSON.stringify(body),
    signal,
  })
}
