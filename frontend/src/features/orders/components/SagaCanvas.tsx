import { useEffect, useState, useSyncExternalStore } from 'react'

import { CANVAS_EDGES, CANVAS_NODES, HOP_DURATION_MS, deriveHopPlan } from '../canvas'
import type { OrderStatus } from '../saga'

/**
 * The live saga canvas (#207): the REAL RabbitMQ topology — services, fanout exchanges,
 * per-consumer queues, all under their production names — with this order's journey
 * replayed across it hop by hop. Everything drawn is driven by the polled order status;
 * only the pacing is theatrical (the saga settles faster than a human can watch).
 *
 * Pacing is viewer-controlled: auto-play advances every HOP_DURATION_MS, Pause freezes it,
 * Next steps one hop immediately — no single tempo suits both glancing and reading.
 * Animation is CSS-only (stroke-dash draw-on via pathLength=100); prefers-reduced-motion
 * renders every reached hop lit with no movement and no timers.
 */

// prefers-reduced-motion, read as an external store (frontend canon: useSyncExternalStore,
// not addEventListener-in-effect).
const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)'

function subscribeToReducedMotion(onChange: () => void): () => void {
  if (typeof window.matchMedia !== 'function') {
    return () => {
      /* matchMedia unavailable (jsdom) — nothing to unsubscribe */
    }
  }
  const media = window.matchMedia(REDUCED_MOTION_QUERY)
  media.addEventListener('change', onChange)
  return () => {
    media.removeEventListener('change', onChange)
  }
}

function readReducedMotion(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia(REDUCED_MOTION_QUERY).matches
}

type EdgeState = 'idle' | 'playing' | 'done'
type EdgeStates = Record<string, EdgeState>

function edgeStates(playedHops: number, playingHop: number, plan: ReturnType<typeof deriveHopPlan>): EdgeStates {
  const states: EdgeStates = {}
  plan.forEach((hop, index) => {
    const state: EdgeState | null = index < playedHops ? 'done' : index === playingHop ? 'playing' : null
    if (state != null) {
      for (const edge of [hop.publishEdge, ...hop.fanEdges]) {
        states[edge] = state
      }
    }
  })
  return states
}

export function SagaCanvas({ status }: Readonly<{ status: OrderStatus }>) {
  const plan = deriveHopPlan(status)
  const failed = status === 'PaymentFailed'
  // How many hops have finished ANIMATING (may lag the real status — that's the point).
  const [animatedHops, setAnimatedHops] = useState(0)
  const [paused, setPaused] = useState(false)
  const reducedMotion = useSyncExternalStore(subscribeToReducedMotion, readReducedMotion)
  // Under reduced motion the replay is skipped entirely — every reached hop renders lit.
  const playedHops = reducedMotion ? plan.length : animatedHops

  // Auto-play: advance one hop per tick unless paused. A timer IS the external system
  // being synchronized with here (wall-clock pacing).
  useEffect(() => {
    if (reducedMotion || paused || animatedHops >= plan.length) return undefined
    const timer = setTimeout(() => {
      setAnimatedHops((played) => played + 1)
    }, HOP_DURATION_MS)
    return () => {
      clearTimeout(timer)
    }
  }, [animatedHops, plan.length, reducedMotion, paused])

  const playingHop = playedHops < plan.length ? playedHops : -1
  const edges = edgeStates(playedHops, playingHop, plan)
  const activeHop = playingHop >= 0 ? plan[playingHop] : plan[plan.length - 1]
  const litNodes = new Set<string>()
  plan.forEach((hop, index) => {
    if (index <= playingHop || playedHops > index) {
      hop.consumers.forEach((consumer) => {
        litNodes.add(consumer)
      })
    }
  })
  litNodes.add('order')

  const controlClasses = 'rounded border border-slate-600 px-2.5 py-1 text-sm text-slate-300 hover:bg-slate-800'

  return (
    // Breaks out of the max-w-2xl article column at every width — SVG text scales with
    // container width, and at column width the labels were unreadable (live-demo feedback).
    <section
      aria-label="Live saga canvas"
      className="relative left-1/2 w-[min(1000px,calc(100vw-2rem))] -translate-x-1/2 overflow-hidden rounded-lg border border-slate-700 bg-slate-900"
    >
      <style>{`
        @keyframes saga-draw { to { stroke-dashoffset: 0; } }
        @keyframes saga-pulse { 0%,100% { opacity: .55; } 50% { opacity: 1; } }
        .saga-edge { stroke-dasharray: 100; stroke-dashoffset: 100; }
        .saga-edge[data-state="playing"] { animation: saga-draw 1100ms ease-out forwards; }
        /* Fan edges wait for the publish edge to reach the exchange, so the hop reads in order. */
        .saga-edge[data-role="fan"][data-state="playing"] { animation-delay: 900ms; }
        .saga-edge[data-state="done"] { stroke-dashoffset: 0; }
        .saga-node-active rect, .saga-node-active path { animation: saga-pulse 1.4s ease-in-out infinite; }
        @media (prefers-reduced-motion: reduce) {
          .saga-edge[data-state="playing"] { animation: none; stroke-dashoffset: 0; }
          .saga-node-active rect, .saga-node-active path { animation: none; }
        }
      `}</style>
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-700/60 px-4 py-2">
        <p className="text-base font-semibold text-slate-200">
          Live saga canvas
          <span className="ml-2 hidden font-normal text-slate-400 sm:inline">the real topology, under production names</span>
        </p>
        {!reducedMotion && (
          <div className="flex items-center gap-2">
            <span className="mr-1 text-xs text-slate-500" aria-live="polite">
              {playingHop >= 0 ? `hop ${String(playingHop + 1)} of ${String(plan.length)}` : 'complete'}
            </span>
            <button
              type="button"
              className={controlClasses}
              onClick={() => {
                setAnimatedHops((played) => Math.min(played + 1, plan.length))
              }}
            >
              ⏭ Next hop
            </button>
            <button
              type="button"
              className={controlClasses}
              onClick={() => {
                setPaused((wasPaused) => !wasPaused)
              }}
            >
              {paused ? '▶ Auto-play' : '⏸ Pause'}
            </button>
            <button
              type="button"
              className={controlClasses}
              onClick={() => {
                setAnimatedHops(0)
              }}
            >
              ↺ Replay
            </button>
          </div>
        )}
      </div>

      {/* min-width + sideways scroll: the diagram never shrinks below legibility on a
          narrow window — wide content scrolls in its own container. */}
      <div className="overflow-x-auto">
        <svg viewBox="0 0 1000 780" role="img" aria-label="Event flow between services" className="w-full min-w-[760px]">
          {/* RabbitMQ band behind the exchange column */}
          <rect x="640" y="120" width="240" height="550" rx="12" fill="#1e293b" opacity="0.5" />
          <text x="760" y="105" textAnchor="middle" fill="#64748b" fontSize="16" fontWeight="600">RabbitMQ</text>

          {CANVAS_EDGES.map((edge) => {
            const state = edges[edge.id] ?? 'idle'
            const stroke = state === 'idle' ? '#334155' : failed && (edge.id === 'pe-order' || edge.id === 'pe-notify') ? '#f87171' : '#34d399'
            return (
              <g key={edge.id}>
                <path d={edge.d} fill="none" stroke="#334155" strokeWidth="1.5" />
                {state !== 'idle' && (
                  <path className="saga-edge" data-state={state} data-role={edge.role} d={edge.d} pathLength={100} fill="none" stroke={stroke} strokeWidth="3.5" opacity={state === 'done' ? 0.5 : 1} />
                )}
                {edge.queue != null && state !== 'idle' && <QueueLabel edge={edge.id} queue={edge.queue} />}
              </g>
            )
          })}

          {CANVAS_NODES.map((node) => {
            const active = node.kind !== 'exchange' && litNodes.has(node.id)
            const isPlayingConsumer = playingHop >= 0 && plan[playingHop]?.consumers.includes(node.id)
            if (node.kind === 'exchange') {
              const engaged = Object.entries(edges).some(([id, s]) => s !== 'idle' && id.includes(node.id))
              return (
                <g key={node.id} transform={`translate(${String(node.x)} ${String(node.y)})`}>
                  <path d="M 0 -40 L 40 0 L 0 40 L -40 0 Z" fill={engaged ? '#f59e0b' : '#475569'} opacity={engaged ? 0.9 : 0.6} />
                  <text y="6" textAnchor="middle" fill="#0f172a" fontSize="15" fontWeight="700">⤨</text>
                  <text y="64" textAnchor="middle" fill="#cbd5e1" fontSize="17" fontFamily="ui-monospace, monospace">{node.label}</text>
                  <text y="82" textAnchor="middle" fill="#64748b" fontSize="13">{node.sublabel}</text>
                </g>
              )
            }
            return (
              <g key={node.id} transform={`translate(${String(node.x)} ${String(node.y)})`} className={isPlayingConsumer ? 'saga-node-active' : undefined}>
                <rect x="-130" y="-34" width="260" height="68" rx="10" fill={active ? '#0f766e' : '#1e293b'} stroke={active ? '#2dd4bf' : '#475569'} strokeWidth="1.5" />
                <text y="-4" textAnchor="middle" fill="#e2e8f0" fontSize="19" fontWeight="600">{node.label}</text>
                <text y="20" textAnchor="middle" fill="#94a3b8" fontSize="13.5">{node.sublabel}</text>
              </g>
            )
          })}

          {/* The event currently in flight */}
          {activeHop != null && playingHop >= 0 && (
            <text x="500" y="762" textAnchor="middle" fill={failed && activeHop.id === 'payment-failed' ? '#f87171' : '#34d399'} fontSize="20" fontWeight="600">
              ⚡ {activeHop.event} in flight
            </text>
          )}
        </svg>
      </div>

      <div className="border-t border-slate-700/60 px-4 py-3">
        <p className="text-base leading-relaxed text-slate-200" aria-live="polite">
          {activeHop?.caption}
        </p>
        <p className="mt-1.5 text-sm text-slate-500">
          Exactly-once delivery is impossible in a distributed system — this achieves exactly-once <em>processing</em>: every failure mode is
          pushed toward duplication (outbox + durable queues), and duplication is made a no-op (idempotent consumers).
        </p>
      </div>
    </section>
  )
}

/** Queue-name label positioned along its fan edge, where the vertical layout has room. */
const QUEUE_LABEL_POSITIONS: Record<string, { x: number; y: number }> = {
  'oe-payment': { x: 490, y: 218 },
  'oe-notify': { x: 902, y: 450 },
  'pe-shipping': { x: 490, y: 418 },
  'pe-order': { x: 452, y: 300 },
  'pe-notify': { x: 878, y: 585 },
  'se-order': { x: 120, y: 400 },
  'se-notify': { x: 560, y: 720 },
}

function QueueLabel({ edge, queue }: Readonly<{ edge: string; queue: string }>) {
  const pos = QUEUE_LABEL_POSITIONS[edge]
  if (pos == null) return null
  return (
    <text x={pos.x} y={pos.y} textAnchor="middle" fill="#7dd3fc" fontSize="15" fontFamily="ui-monospace, monospace">
      {queue}
    </text>
  )
}
