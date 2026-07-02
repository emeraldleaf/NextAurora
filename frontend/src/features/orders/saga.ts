// The saga-narrator model: what the timeline shows and what each step teaches.
//
// This feature IS the documentation (frontend/CLAUDE.md "Conventions") — the narration
// strings explain, in the UI, what the backend just did. They mirror the real mechanics
// in OrderService/PaymentService/ShippingService: transactional outbox, RabbitMQ fanout
// exchanges, idempotent consumers, choreography (no orchestrator). If the backend's saga
// changes shape, this copy changes with it — it's a projection of the architecture, not
// marketing text.

/** Mirror of OrderService.Domain.OrderStatus — persisted (and serialized) as strings. */
export type OrderStatus = 'Placed' | 'Paid' | 'Shipped' | 'Delivered' | 'Cancelled' | 'PaymentFailed'

export type StepState = 'complete' | 'active' | 'pending' | 'failed'

export interface SagaStep {
  status: OrderStatus
  title: string
  /** What the backend just did to reach this step — shown in the narrator panel. */
  narration: string
}

/** The happy-path choreography, in order. Failure/cancel states overlay onto these. */
export const SAGA_STEPS: readonly SagaStep[] = [
  {
    status: 'Placed',
    title: 'Placed',
    narration:
      'POST /orders returned 202 Accepted, not 201 — placement is asynchronous and the order row itself is the tracking record. ' +
      'OrderService saved the order and staged OrderPlacedEvent in the SAME database transaction (transactional outbox — the event cannot be lost, even if the broker is down), ' +
      'then Wolverine published it to the order-events fanout exchange on RabbitMQ. This page is polling while the saga runs.',
  },
  {
    status: 'Paid',
    title: 'Paid',
    narration:
      'PaymentService consumed OrderPlacedEvent from its payment-orders queue, processed the payment, and published PaymentCompletedEvent to the payment-events exchange. ' +
      'OrderService consumed that and marked the order Paid. Delivery is at-least-once, so the handler is idempotent — a duplicate event hits the status guard and is ignored.',
  },
  {
    status: 'Shipped',
    title: 'Shipped',
    narration:
      'ShippingService reacted to the same PaymentCompletedEvent (its own queue on the fanout exchange), dispatched a shipment, and published ShipmentDispatchedEvent; ' +
      'OrderService marked the order Shipped. No orchestrator coordinated any of this — every service just reacted to events. That is the choreography saga.',
  },
]

/** Narration for the non-happy-path terminals (rendered in place of the next step's panel). */
export const TERMINAL_NARRATION: Partial<Record<OrderStatus, string>> = {
  PaymentFailed:
    'PaymentService declined the payment and published PaymentFailedEvent; OrderService consumed it and marked the order PaymentFailed. ' +
    'This is the saga’s failure branch — same event mechanics as the happy path, different event.',
  Cancelled: 'The order was cancelled before it shipped. Cancel is allowed from any non-shipped state.',
}

/** How far along the happy path a status is. Delivered sits past Shipped; terminals sit where they interrupted. */
const PROGRESS: Record<OrderStatus, number> = {
  Placed: 0,
  Paid: 1,
  Shipped: 2,
  Delivered: 3,
  // Failure terminals: how many happy steps completed before the saga stopped.
  PaymentFailed: 1, // Placed completed; payment step failed.
  Cancelled: 1, // Placed completed; cancelled before shipping.
}

/** The saga stopped moving — stop polling. */
export function isSagaSettled(status: OrderStatus): boolean {
  return status === 'Shipped' || status === 'Delivered' || status === 'Cancelled' || status === 'PaymentFailed'
}

const FAILURE_TERMINALS: ReadonlySet<OrderStatus> = new Set(['PaymentFailed', 'Cancelled'])

/**
 * Derive each happy-path step's visual state from the order's current status.
 * A step is complete once reached, active if it's the furthest reached and the saga is
 * still moving, failed if the saga terminated at that step, pending otherwise.
 */
export function deriveStepStates(status: OrderStatus): StepState[] {
  const progress = PROGRESS[status]
  return SAGA_STEPS.map((_, index) => {
    if (index < progress) return 'complete'
    if (index === progress) {
      if (FAILURE_TERMINALS.has(status)) return 'failed'
      // The furthest-reached step: completed if the saga already moved past or settled
      // here (Shipped/Delivered), otherwise it's the live step we're polling on.
      return isSagaSettled(status) ? 'complete' : 'active'
    }
    return 'pending'
  })
}
