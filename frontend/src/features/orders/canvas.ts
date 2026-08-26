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

// Layout: Coordinates are viewBox units (0 0 1000 780): services stacked left, exchanges right —
// a vertical layout gives every label a full column of room, so type stays large.
export const CANVAS_NODES: readonly CanvasNode[] = [
  { id: 'order', label: 'OrderService', sublabel: 'SQL Server + outbox', kind: 'service', x: 170, y: 90 },
  { id: 'payment', label: 'PaymentService', sublabel: 'SQL Server + outbox', kind: 'service', x: 170, y: 290 },
  { id: 'shipping', label: 'ShippingService', sublabel: 'Postgres + outbox', kind: 'service', x: 170, y: 490 },
  { id: 'notify', label: 'NotificationService', sublabel: 'stateless consumer', kind: 'service', x: 170, y: 690 },
  { id: 'oe', label: 'order-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 190 },
  { id: 'pe', label: 'payment-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 390 },
  { id: 'se', label: 'shipping-events', sublabel: 'fanout', kind: 'exchange', x: 760, y: 590 },
]

export const CANVAS_EDGES: readonly CanvasEdge[] = [
  // h0 — OrderPlaced
  { id: 'order-oe', d: 'M 300 90 Q 620 90 760 150', role: 'publish' },
  { id: 'oe-payment', d: 'M 720 190 Q 480 220 300 272', role: 'fan', queue: 'payment-orders' },
  { id: 'oe-notify', d: 'M 760 230 Q 930 460 302 682', role: 'fan', queue: 'notify-orders' },
  // h1 — PaymentCompleted (and the PaymentFailed variant reuses pe-order/pe-notify)
  { id: 'payment-pe', d: 'M 300 308 Q 620 308 760 350', role: 'publish' },
  { id: 'pe-shipping', d: 'M 720 390 Q 480 420 300 472', role: 'fan', queue: 'shipping-payments' },
  { id: 'pe-order', d: 'M 722 382 Q 430 330 300 114', role: 'fan', queue: 'order-payments' },
  { id: 'pe-notify', d: 'M 760 430 Q 900 570 302 692', role: 'fan', queue: 'notify-payments' },
  // h2 — ShipmentDispatched
  { id: 'shipping-se', d: 'M 300 508 Q 620 508 760 550', role: 'publish' },
  { id: 'se-order', d: 'M 720 594 Q 10 470 168 122', role: 'fan', queue: 'order-shipping' },
  { id: 'se-notify', d: 'M 760 630 Q 790 690 302 700', role: 'fan', queue: 'notify-shipping' },
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

/** ms per hop on auto-play. The viewer can also Pause or step with Next (live-demo feedback:
 * no single tempo suits both watching and reading — controls beat tuning). */
export const HOP_DURATION_MS = 4500
