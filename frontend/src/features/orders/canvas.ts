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
  publishEdge: string
  fanEdges: string[]
  /** Nodes that light up as this hop's consumers. */
  consumers: string[]
  caption: string
}

// Layout: chronological pipeline left→right (service → exchange → next service …),
// Notification below collecting from every exchange, return edges arcing over the top back
// to OrderService. Coordinates are viewBox units (0 0 1080 440).
export const CANVAS_NODES: readonly CanvasNode[] = [
  { id: 'order', label: 'OrderService', sublabel: 'SQL Server + outbox', kind: 'service', x: 95, y: 130 },
  { id: 'oe', label: 'order-events', sublabel: 'fanout', kind: 'exchange', x: 275, y: 130 },
  { id: 'payment', label: 'PaymentService', sublabel: 'SQL Server + outbox', kind: 'service', x: 455, y: 130 },
  { id: 'pe', label: 'payment-events', sublabel: 'fanout', kind: 'exchange', x: 635, y: 130 },
  { id: 'shipping', label: 'ShippingService', sublabel: 'Postgres + outbox', kind: 'service', x: 815, y: 130 },
  { id: 'se', label: 'shipping-events', sublabel: 'fanout', kind: 'exchange', x: 995, y: 130 },
  { id: 'notify', label: 'NotificationService', sublabel: 'stateless consumer', kind: 'service', x: 540, y: 368 },
]

export const CANVAS_EDGES: readonly CanvasEdge[] = [
  // h0 — OrderPlaced
  { id: 'order-oe', d: 'M 153 130 L 245 130', role: 'publish' },
  { id: 'oe-payment', d: 'M 305 130 L 397 130', role: 'fan', queue: 'payment-orders' },
  { id: 'oe-notify', d: 'M 275 160 Q 275 330 482 360', role: 'fan', queue: 'notify-orders' },
  // h1 — PaymentCompleted (and the PaymentFailed variant reuses pe-order/pe-notify)
  { id: 'payment-pe', d: 'M 513 130 L 605 130', role: 'publish' },
  { id: 'pe-shipping', d: 'M 665 130 L 757 130', role: 'fan', queue: 'shipping-payments' },
  { id: 'pe-order', d: 'M 635 100 Q 635 36 375 36 Q 115 36 115 102', role: 'fan', queue: 'order-payments' },
  { id: 'pe-notify', d: 'M 635 160 Q 635 330 598 360', role: 'fan', queue: 'notify-payments' },
  // h2 — ShipmentDispatched
  { id: 'shipping-se', d: 'M 873 130 L 965 130', role: 'publish' },
  { id: 'se-order', d: 'M 995 100 Q 995 10 535 10 Q 75 10 75 102', role: 'fan', queue: 'order-shipping' },
  { id: 'se-notify', d: 'M 995 160 Q 995 340 598 368', role: 'fan', queue: 'notify-shipping' },
]

const PLACED_HOP: Hop = {
  id: 'placed',
  event: 'OrderPlacedEvent',
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

/** ms per hop when replaying — slow enough to read every label, per live-demo feedback. */
export const HOP_DURATION_MS = 3400
