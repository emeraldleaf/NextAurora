import { SAGA_STEPS, TERMINAL_NARRATION, deriveStepStates, isSagaSettled, type OrderStatus, type StepState } from '../saga'

/**
 * The saga narrator (epic #130 Phase 3): a Placed → Paid → Shipped timeline that renders
 * live off the polled order status, plus a panel explaining what the backend just did to
 * reach the current step. The demo IS the engineering — this component turns the
 * choreography saga (outbox → RabbitMQ fanout → idempotent consumers) into something you
 * can watch happen.
 *
 * Pure render off the status prop: polling cadence lives in orderByIdQuery, narration
 * copy + step derivation live in ../saga.ts. This file only draws.
 */

const DOT_CLASSES: Record<StepState, string> = {
  complete: 'bg-emerald-600 text-white',
  active: 'animate-pulse bg-blue-600 text-white',
  failed: 'bg-red-600 text-white',
  pending: 'bg-zinc-200 text-zinc-500',
}

function dotGlyph(state: StepState, index: number): string {
  if (state === 'complete') return '✓'
  if (state === 'failed') return '✕'
  return String(index + 1)
}

export function SagaTimeline({ status }: Readonly<{ status: OrderStatus }>) {
  const stepStates = deriveStepStates(status)
  const settled = isSagaSettled(status)
  const terminalNarration = TERMINAL_NARRATION[status]
  // Every step the saga has reached keeps its narration on screen. The saga can settle in
  // ~2 seconds — far faster than anyone reads — so the panel is a persistent step-by-step
  // log, never a transient status line that the next event overwrites (#207).
  const reachedSteps = SAGA_STEPS.filter((_, index) => stepStates[index] !== 'pending')
  const panelClasses =
    terminalNarration == null ? 'border-zinc-200 bg-zinc-50 text-zinc-700' : 'border-red-200 bg-red-50 text-red-900'
  const panelHeading = terminalNarration == null ? 'What the backend did — step by step' : `Saga stopped: ${status}`

  return (
    <section aria-label="Order saga progress" className="space-y-3">
      <ol className="flex items-center gap-0">
        {SAGA_STEPS.map((step, index) => {
          // deriveStepStates maps 1:1 over SAGA_STEPS; the fallback only satisfies
          // noUncheckedIndexedAccess (the index can't actually miss).
          const state = stepStates[index] ?? 'pending'
          return (
            <li key={step.status} className="flex flex-1 items-center" aria-current={state === 'active' ? 'step' : undefined}>
              <span
                className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-semibold ${DOT_CLASSES[state]}`}
              >
                {dotGlyph(state, index)}
              </span>
              <span className="ml-2 text-sm font-medium">
                {step.title}
                {/* Live step gets a visually-quiet hint that the page is polling. */}
                {state === 'active' && <span className="ml-1 text-xs font-normal text-zinc-500">(in progress…)</span>}
              </span>
              {index < SAGA_STEPS.length - 1 && (
                <span aria-hidden className={`mx-3 h-px flex-1 ${stepStates[index + 1] === 'pending' ? 'bg-zinc-200' : 'bg-emerald-600'}`} />
              )}
            </li>
          )
        })}
      </ol>

      {status === 'Delivered' && <p className="text-sm text-zinc-600">Delivered — the saga completed end-to-end.</p>}

      {(reachedSteps.length > 0 || terminalNarration != null) && (
        <aside aria-label="What the backend just did" className={`rounded-md border p-3 text-sm leading-relaxed ${panelClasses}`}>
          <p className="mb-1 font-semibold">{panelHeading}</p>
          <ol className="space-y-2">
            {reachedSteps.map((step) => (
              <li key={step.status}>
                <span className="font-medium">{step.title} — </span>
                {step.narration}
              </li>
            ))}
            {terminalNarration != null && <li className="font-medium">{terminalNarration}</li>}
          </ol>
          {settled ? null : (
            <p className="mt-2 text-xs text-zinc-500">
              This page polls the order every 2 s — the status you're watching advances as each service consumes and re-publishes events.
            </p>
          )}
        </aside>
      )}
    </section>
  )
}
