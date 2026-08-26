import { useEffect, useState, useSyncExternalStore } from 'react'

import { CANVAS_EDGES, CANVAS_NODES, HOP_DURATION_MS, SERVICE_COLORS, SERVICE_FILLS, deriveHopPlan } from '../canvas'
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

/** Each engaged edge renders in its publishing service's color (failure hop → red). */
function edgeColors(plan: ReturnType<typeof deriveHopPlan>): Record<string, string> {
  const colors: Record<string, string> = {}
  for (const hop of plan) {
    const color = hop.id === 'payment-failed' ? '#f87171' : (SERVICE_COLORS[hop.publisher] ?? '#34d399')
    for (const edge of [hop.publishEdge, ...hop.fanEdges]) {
      colors[edge] = color
    }
  }
  return colors
}

export function SagaCanvas({ status }: Readonly<{ status: OrderStatus }>) {
  const plan = deriveHopPlan(status)
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
  const hopColors = edgeColors(plan)
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
          {activeHop != null && playingHop >= 0 ? (
            <span
              className="ml-3 font-semibold"
              style={{ color: activeHop.id === 'payment-failed' ? '#f87171' : (SERVICE_COLORS[activeHop.publisher] ?? '#34d399') }}
            >
              ⚡ {activeHop.event} in flight
            </span>
          ) : (
            <span className="ml-2 hidden font-normal text-slate-400 sm:inline">the real topology, under production names</span>
          )}
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
        <svg viewBox="0 0 1000 560" role="img" aria-label="Event flow between services" className="w-full min-w-[760px]">
          {/* RabbitMQ band behind the exchange column */}
          <rect x="640" y="85" width="240" height="385" rx="12" fill="#1e293b" opacity="0.5" />
          <text x="760" y="72" textAnchor="middle" fill="#64748b" fontSize="16" fontWeight="600">RabbitMQ</text>

          {CANVAS_EDGES.map((edge) => {
            const state = edges[edge.id] ?? 'idle'
            const stroke = state === 'idle' ? '#334155' : (hopColors[edge.id] ?? '#34d399')
            return (
              <g key={edge.id}>
                <path d={edge.d} fill="none" stroke="#334155" strokeWidth="1.5" />
                {state !== 'idle' && (
                  <path className="saga-edge" data-state={state} data-role={edge.role} d={edge.d} pathLength={100} fill="none" stroke={stroke} strokeWidth="3.5" opacity={state === 'done' ? 0.5 : 1} />
                )}
                {edge.queue != null && state !== 'idle' && <QueueLabel edge={edge.id} queue={edge.queue} color={stroke} />}
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
                  <path d="M 0 -38 L 38 0 L 0 38 L -38 0 Z" fill={engaged ? '#f59e0b' : '#475569'} opacity={engaged ? 0.9 : 0.6} />
                  <text y="6" textAnchor="middle" fill="#0f172a" fontSize="15" fontWeight="700">⤨</text>
                  <text y="58" textAnchor="middle" fill="#cbd5e1" fontSize="17" fontFamily="ui-monospace, monospace">{node.label}</text>
                  <text y="76" textAnchor="middle" fill="#64748b" fontSize="13">{node.sublabel}</text>
                </g>
              )
            }
            return (
              <g key={node.id} transform={`translate(${String(node.x)} ${String(node.y)})`} className={isPlayingConsumer ? 'saga-node-active' : undefined}>
                <rect
                  x="-130" y="-34" width="260" height="68" rx="10"
                  fill={active ? (SERVICE_FILLS[node.id] ?? '#0f766e') : '#1e293b'}
                  stroke={active ? (SERVICE_COLORS[node.id] ?? '#2dd4bf') : '#475569'}
                  strokeWidth="2"
                />
                <text y="-4" textAnchor="middle" fill={active ? (SERVICE_COLORS[node.id] ?? '#e2e8f0') : '#e2e8f0'} fontSize="19" fontWeight="600">{node.label}</text>
                <text y="20" textAnchor="middle" fill="#94a3b8" fontSize="13.5">{node.sublabel}</text>
              </g>
            )
          })}

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
  'oe-payment': { x: 492, y: 148 },
  'oe-notify': { x: 905, y: 320 },
  'pe-shipping': { x: 492, y: 283 },
  'pe-order': { x: 470, y: 190 },
  'pe-notify': { x: 880, y: 430 },
  'se-order': { x: 108, y: 300 },
  'se-notify': { x: 660, y: 508 },
}

function QueueLabel({ edge, queue, color }: Readonly<{ edge: string; queue: string; color: string }>) {
  const pos = QUEUE_LABEL_POSITIONS[edge]
  if (pos == null) return null
  return (
    <text x={pos.x} y={pos.y} textAnchor="middle" fill={color} fontSize="15" fontFamily="ui-monospace, monospace">
      {queue}
    </text>
  )
}
