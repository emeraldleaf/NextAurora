// The saga-canvas model: the REAL RabbitMQ topology (mirrors NextAurora.Contracts
// MessagingExchanges/MessagingQueues — one fanout exchange per event family, one queue per
// consumer, named {consumer}-{source}) plus the animation plan that replays a given order's
// journey across it. Like saga.ts, this is a projection of the architecture, not decoration:
// if the backend topology changes shape, this model changes with it.
//
// Why a paced REPLAY instead of live capture: the saga settles in ~2s — faster than the 2s
// poll, let alone a human eye. The canvas animates the hops the order has genuinely reached
// (derived from the polled status), one at a time, so the flow is watchable at any speed the
// backend runs. Nothing is invented; only the pacing is theatrical.

import type { OrderStatus } from './saga'

export interface CanvasNode {
  id: string
  label: string
  sublabel?: string
  kind: 'service' | 'exchange' | 'db'
  x: number
  y: number
}

/** One SVG path an event travels during a hop (publish edge or fan-out edge). */
export interface CanvasEdge {
  id: string
  /** SVG path d-string. All paths use pathLength=100 so dash animation is uniform. */
  d: string
  /** publish = service→exchange; fan = exchange→consumer (starts after the publish draws). */
  role: 'publish' | 'fan'
  /** Queue name shown at the consuming end (real names — {consumer}-{source}). */
  queue?: string
}

/** One publish: a service commits + publishes an event, the exchange fans it out. */
export interface Hop {
  id: string
  event: string
  /** Service that published this hop's event — edges render in its color. */
  publisher: 'order' | 'payment' | 'shipping'
  publishEdge: string
  fanEdges: string[]
  /** Nodes that light up as this hop's consumers. */
  consumers: string[]
  caption: string
}

// Layout: Coordinates are viewBox units (0 0 1000 560): services stacked left, exchanges right —
// full column of room per label (type stays large) at a height that fits one screen.
export const CANVAS_NODES: readonly CanvasNode[] = [
  { id: 'order', label: 'OrderService', sublabel: 'SQL Server + outbox', kind: 'service', x: 170, y: 70 },
  { id: 'payment', label: 'PaymentService', sublabel: 'SQL Server + outbox', kind: 'service', x: 170, y: 205 },
  { id: 'shipping', label: 'ShippingService', sublabel: 'Postgres + outbox', kind: 'service', x: 170, y: 340 },
  { id: 'notify', label: 'NotificationService', sublabel: 'stateless consumer', kind: 'service', x: 170, y: 475 },
  { id: 'oe', label: 'order-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 137 },
  { id: 'pe', label: 'payment-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 272 },
  { id: 'se', label: 'shipping-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 407 },
]

export const CANVAS_EDGES: readonly CanvasEdge[] = [
  // h0 — OrderPlaced
  { id: 'order-oe', d: 'M 300 70 Q 620 70 760 99', role: 'publish' },
  { id: 'oe-payment', d: 'M 722 137 Q 480 155 300 190', role: 'fan', queue: 'payment-orders' },
  { id: 'oe-notify', d: 'M 760 175 Q 950 330 302 468', role: 'fan', queue: 'notify-orders' },
  // h1 — PaymentCompleted (and the PaymentFailed variant reuses pe-order/pe-notify)
  { id: 'payment-pe', d: 'M 300 220 Q 620 220 760 234', role: 'publish' },
  { id: 'pe-shipping', d: 'M 722 272 Q 480 292 300 325', role: 'fan', queue: 'shipping-payments' },
  { id: 'pe-order', d: 'M 722 265 Q 430 220 300 90', role: 'fan', queue: 'order-payments' },
  { id: 'pe-notify', d: 'M 760 310 Q 900 420 302 477', role: 'fan', queue: 'notify-payments' },
  // h2 — ShipmentDispatched
  { id: 'shipping-se', d: 'M 300 355 Q 620 355 760 369', role: 'publish' },
  { id: 'se-order', d: 'M 722 412 Q 10 330 168 95', role: 'fan', queue: 'order-shipping' },
  { id: 'se-notify', d: 'M 760 445 Q 790 490 302 487', role: 'fan', queue: 'notify-shipping' },
]

const PLACED_HOP: Hop = {
  id: 'placed',
  event: 'OrderPlacedEvent',
  publisher: 'order',
  publishEdge: 'order-oe',
  fanEdges: ['oe-payment', 'oe-notify'],
  consumers: ['payment', 'notify'],
  caption:
    'OrderService committed the order row AND the event in one database transaction (transactional outbox), ' +
    'then published to the order-events fanout exchange. Two queues each get their own copy.',
}

const PAID_HOP: Hop = {
  id: 'paid',
  event: 'PaymentCompletedEvent',
  publisher: 'payment',
  publishEdge: 'payment-pe',
  fanEdges: ['pe-shipping', 'pe-order', 'pe-notify'],
  consumers: ['shipping', 'order', 'notify'],
  caption:
    'PaymentService consumed OrderPlaced, processed payment, published PaymentCompleted. Delivery is ' +
    'at-least-once — every consumer is idempotent, so a redelivery is a no-op, never a double charge.',
}

const SHIPPED_HOP: Hop = {
  id: 'shipped',
  event: 'ShipmentDispatchedEvent',
  publisher: 'shipping',
  publishEdge: 'shipping-se',
  fanEdges: ['se-order', 'se-notify'],
  consumers: ['order', 'notify'],
  caption:
    'ShippingService reacted to the SAME PaymentCompleted event on its own queue and dispatched the shipment. ' +
    'No orchestrator told any service what to do — this is choreography.',
}

export const HOPS: readonly Hop[] = [PLACED_HOP, PAID_HOP, SHIPPED_HOP]

/** The failure branch: replaces the 'paid' hop when the saga died at payment. */
export const FAILED_HOP: Hop = {
  id: 'payment-failed',
  event: 'PaymentFailedEvent',
  publisher: 'payment',
  publishEdge: 'payment-pe',
  fanEdges: ['pe-order', 'pe-notify'],
  consumers: ['order', 'notify'],
  caption:
    'PaymentService declined and published PaymentFailedEvent — the failure branch rides the exact same ' +
    'outbox + fanout mechanics as the happy path.',
}

/** Which hops this order has genuinely reached, given its polled status. */
export function deriveHopPlan(status: OrderStatus): readonly Hop[] {
  switch (status) {
    case 'Placed':
      return [PLACED_HOP]
    case 'Paid':
      return [PLACED_HOP, PAID_HOP]
    case 'Shipped':
    case 'Delivered':
      return HOPS
    case 'PaymentFailed':
      return [PLACED_HOP, FAILED_HOP]
    case 'Cancelled':
      return [PLACED_HOP]
  }
}

/**
 * Per-service identity colors: nodes wear them, and every edge/queue-label/badge renders in
 * the color of the service that PUBLISHED the event riding it — the choreography becomes
 * legible by hue ("violet = Payment said something"). Failure branch overrides to red.
 */
export const SERVICE_COLORS: Record<string, string> = {
  order: '#38bdf8',
  payment: '#a78bfa',
  shipping: '#34d399',
  notify: '#fb7185',
}

/** Darker companion fills for an active node's body (accent goes on the border/text). */
export const SERVICE_FILLS: Record<string, string> = {
  order: '#0c4a6e',
  payment: '#3b0764',
  shipping: '#064e3b',
  notify: '#4c0519',
}

/** ms per hop on auto-play. The viewer can also Pause or step with Next (live-demo feedback:
 * no single tempo suits both watching and reading — controls beat tuning). */
export const HOP_DURATION_MS = 4500
