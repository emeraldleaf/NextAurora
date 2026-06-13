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

export async function getJson<T>(baseUrl: string, path: string, signal?: AbortSignal): Promise<ApiResponse<T>> {
  const response = await fetch(`${baseUrl}${path}`, { signal })
  const correlationId = response.headers.get('X-Correlation-Id')

  if (!response.ok) {
    // RFC 7807 ProblemDetails body — generic detail only by backend contract; the
    // correlation ID is the support handle, so it rides the error.
    throw new ApiError(`Request failed with status ${String(response.status)}`, response.status, correlationId)
  }

  return { data: (await response.json()) as T, correlationId }
}
