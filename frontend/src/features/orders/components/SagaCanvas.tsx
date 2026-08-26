import { useEffect, useState, useSyncExternalStore } from 'react'

import { CANVAS_EDGES, CANVAS_NODES, HOP_DURATION_MS, deriveHopPlan } from '../canvas'
import type { OrderStatus } from '../saga'

/**
 * The live saga canvas (#207): the REAL RabbitMQ topology — services, fanout exchanges,
 * per-consumer queues, all under their production names — with this order's journey
 * replayed across it hop by hop. Everything drawn is driven by the polled order status;
 * only the pacing is theatrical (the saga settles faster than a human can watch, so
 * reached hops play back at HOP_DURATION_MS — see ../canvas.ts).
 *
 * Animation is CSS-only (stroke-dash draw-on via pathLength=100, keyframes in the local
 * <style> block) — no graph/animation library for 7 nodes. prefers-reduced-motion renders
 * every reached hop lit, no movement.
 */

// prefers-reduced-motion, read as an external store (frontend canon: useSyncExternalStore,
// not addEventListener-in-effect). The CSS block already stills the drawing; this also skips
// the pacing timers, so reduced-motion users get the whole story instantly instead of
// waiting ~10s for captions to arrive (CodeRabbit finding on #211).
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
  const [replayKey, setReplayKey] = useState(0)
  const reducedMotion = useSyncExternalStore(subscribeToReducedMotion, readReducedMotion)
  // Under reduced motion the replay is skipped entirely — every reached hop renders lit.
  const playedHops = reducedMotion ? plan.length : animatedHops

  // Advance the replay one hop at a time toward what the order has really reached.
  // A timer IS the external system being synchronized with here (wall-clock pacing).
  useEffect(() => {
    if (reducedMotion || animatedHops >= plan.length) return undefined
    const timer = setTimeout(() => { setAnimatedHops((played) => played + 1); }, HOP_DURATION_MS)
    return () => { clearTimeout(timer); }
  }, [animatedHops, plan.length, reducedMotion, replayKey])

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

  return (
    // Breaks out of the max-w-2xl article column on wide screens — SVG text scales with
    // container width, and at 672px the labels were unreadably small (live-demo feedback).
    <section
      aria-label="Live saga canvas"
      className="overflow-hidden rounded-lg border border-slate-700 bg-slate-900 lg:relative lg:left-1/2 lg:w-[min(1080px,calc(100vw-3rem))] lg:-translate-x-1/2"
    >
      <style>{`
        @keyframes saga-draw { to { stroke-dashoffset: 0; } }
        @keyframes saga-pulse { 0%,100% { opacity: .55; } 50% { opacity: 1; } }
        .saga-edge { stroke-dasharray: 100; stroke-dashoffset: 100; }
        .saga-edge[data-state="playing"] { animation: saga-draw ${String(HOP_DURATION_MS * 0.35)}ms ease-out forwards; }
        /* Fan edges wait for the publish edge to reach the exchange, so the hop reads left-to-right. */
        .saga-edge[data-role="fan"][data-state="playing"] { animation-delay: ${String(HOP_DURATION_MS * 0.3)}ms; }
        .saga-edge[data-state="done"] { stroke-dashoffset: 0; }
        .saga-node-active rect, .saga-node-active path { animation: saga-pulse 1.4s ease-in-out infinite; }
        @media (prefers-reduced-motion: reduce) {
          .saga-edge[data-state="playing"] { animation: none; stroke-dashoffset: 0; }
          .saga-node-active rect, .saga-node-active path { animation: none; }
        }
      `}</style>
      <div className="flex items-center justify-between border-b border-slate-700/60 px-4 py-2">
        <p className="text-sm font-semibold text-slate-200">
          Live saga canvas <span className="ml-2 font-normal text-slate-400">the real topology — exchanges, queues, and events under their production names</span>
        </p>
        {!reducedMotion && (
        <button
          type="button"
          onClick={() => {
            setAnimatedHops(0)
            setReplayKey((key) => key + 1)
          }}
          className="rounded border border-slate-600 px-2 py-1 text-xs text-slate-300 hover:bg-slate-800"
        >
          ↺ Replay
        </button>
        )}
      </div>

      <svg viewBox="0 0 1080 440" role="img" aria-label="Event flow between services" className="w-full">
        {/* RabbitMQ band behind the exchange diamonds */}
        <rect x="238" y="92" width="792" height="76" rx="10" fill="#1e293b" opacity="0.5" />
        <text x="240" y="84" fill="#64748b" fontSize="12.5">RabbitMQ — one fanout exchange per event family, one durable queue per consumer</text>

        {CANVAS_EDGES.map((edge) => {
          const state = edges[edge.id] ?? 'idle'
          const stroke = state === 'idle' ? '#334155' : failed && (edge.id === 'pe-order' || edge.id === 'pe-notify') ? '#f87171' : '#34d399'
          return (
            <g key={edge.id}>
              <path d={edge.d} fill="none" stroke="#334155" strokeWidth="1.5" />
              {state !== 'idle' && (
                <path className="saga-edge" data-state={state} data-role={edge.role} d={edge.d} pathLength={100} fill="none" stroke={stroke} strokeWidth="3" opacity={state === 'done' ? 0.5 : 1} />
              )}
              {edge.queue != null && state !== 'idle' && (
                <QueueLabel edge={edge.id} queue={edge.queue} />
              )}
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
                <path d="M 0 -30 L 30 0 L 0 30 L -30 0 Z" fill={engaged ? '#f59e0b' : '#475569'} opacity={engaged ? 0.9 : 0.6} />
                <text y="5" textAnchor="middle" fill="#0f172a" fontSize="11" fontWeight="700">⤨</text>
                <text y="50" textAnchor="middle" fill="#94a3b8" fontSize="13" fontFamily="ui-monospace, monospace">{node.label}</text>
              </g>
            )
          }
          return (
            <g key={node.id} transform={`translate(${String(node.x)} ${String(node.y)})`} className={isPlayingConsumer ? 'saga-node-active' : undefined}>
              <rect x="-58" y="-28" width="116" height="56" rx="9" fill={active ? '#0f766e' : '#1e293b'} stroke={active ? '#2dd4bf' : '#475569'} strokeWidth="1.5" />
              <text y="-3" textAnchor="middle" fill="#e2e8f0" fontSize="13.5" fontWeight="600">{node.label}</text>
              <text y="16" textAnchor="middle" fill="#94a3b8" fontSize="11">{node.sublabel}</text>
            </g>
          )
        })}

        {/* The event currently in flight, shown at the publishing edge's exchange */}
        {activeHop != null && playingHop >= 0 && (
          <text x="540" y="428" textAnchor="middle" fill={failed && activeHop.id === 'payment-failed' ? '#f87171' : '#34d399'} fontSize="16" fontWeight="600">
            ⚡ {activeHop.event} in flight
          </text>
        )}
      </svg>

      <div className="border-t border-slate-700/60 px-4 py-3">
        <p className="text-sm leading-relaxed text-slate-300" aria-live="polite">
          {activeHop?.caption}
        </p>
        <p className="mt-1 text-xs text-slate-500">
          Exactly-once delivery is impossible in a distributed system — this achieves exactly-once <em>processing</em>: every failure mode is
          pushed toward duplication (outbox + durable queues), and duplication is made a no-op (idempotent consumers).
        </p>
      </div>
    </section>
  )
}

/** Queue-name label positioned near the consuming end of a fan edge. */
const QUEUE_LABEL_POSITIONS: Record<string, { x: number; y: number }> = {
  'oe-payment': { x: 351, y: 116 },
  'oe-notify': { x: 348, y: 302 },
  'pe-shipping': { x: 711, y: 116 },
  'pe-order': { x: 375, y: 52 },
  'pe-notify': { x: 648, y: 302 },
  'se-order': { x: 535, y: 26 },
  'se-notify': { x: 872, y: 302 },
}

function QueueLabel({ edge, queue }: Readonly<{ edge: string; queue: string }>) {
  const pos = QUEUE_LABEL_POSITIONS[edge]
  if (pos == null) return null
  return (
    <text x={pos.x} y={pos.y} textAnchor="middle" fill="#7dd3fc" fontSize="12" fontFamily="ui-monospace, monospace">
      {queue}
    </text>
  )
}
