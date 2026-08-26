import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { ApiError } from '@/shared/api/http'

import { demoKeys, listenerStatusQuery, pauseListener, resumeListener } from '../api/demo'

/**
 * The kill switch (#208, demo Act 3): kill PaymentService's RabbitMQ consumer, place an
 * order, watch the saga hold while the event sits in the durable payment-orders queue,
 * revive, watch it drain. The panel renders ONLY when the backend exposes the DemoMode
 * endpoints — a 404 means "not a demo deployment" and the whole control disappears.
 *
 * The backend enforces the guardrails (auth, rate limit, Wolverine self-revive after 60s);
 * this panel is just the visible hand on the lever.
 */
export function KillSwitchPanel() {
  const queryClient = useQueryClient()
  const { data, error } = useQuery(listenerStatusQuery())

  const pause = useMutation({
    mutationFn: pauseListener,
    // Mutations update their affected queries in the mutation definition (frontend canon) —
    // the endpoint returns the fresh status, so write it straight into the cache.
    onSuccess: (status) => {
      queryClient.setQueryData(demoKeys.listener, status)
    },
  })
  const resume = useMutation({
    mutationFn: resumeListener,
    onSuccess: (status) => {
      queryClient.setQueryData(demoKeys.listener, status)
    },
  })

  if (error instanceof ApiError && error.status === 404) return null
  if (data == null || data.status === 'Unavailable') return null

  const down = data.status !== 'Accepting'
  const busy = pause.isPending || resume.isPending

  return (
    <aside
      aria-label="Failure injection"
      className="relative left-1/2 flex w-[min(1000px,calc(100vw-2rem))] -translate-x-1/2 flex-wrap items-center justify-between gap-3 rounded-lg border border-slate-700 bg-slate-900 px-4 py-3"
    >
      <div>
        <p className="text-base font-semibold text-slate-200">
          Failure injection
          <span className={`ml-3 text-sm font-medium ${down ? 'text-red-400' : 'text-emerald-400'}`}>
            PaymentService consumer: {down ? 'STOPPED' : 'running'}
          </span>
        </p>
        <p className="mt-0.5 text-sm text-slate-400">
          {down
            ? `Place an order now — it will hold at payment while the event waits in the durable queue. Auto-revives within ${String(data.autoReviveSeconds)}s.`
            : 'Kill the payment consumer, then place an order and watch the system not lose it.'}
        </p>
      </div>
      <button
        type="button"
        disabled={busy}
        onClick={() => {
          if (down) resume.mutate()
          else pause.mutate()
        }}
        className={`rounded border px-3 py-1.5 text-sm font-semibold disabled:opacity-50 ${
          down
            ? 'border-emerald-500 text-emerald-300 hover:bg-emerald-950'
            : 'border-red-500 text-red-300 hover:bg-red-950'
        }`}
      >
        {down ? '⚡ Revive PaymentService' : '💀 Kill PaymentService'}
      </button>
    </aside>
  )
}
