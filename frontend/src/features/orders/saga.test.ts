import { describe, expect, it } from 'vitest'

import { deriveStepStates, isSagaSettled, type OrderStatus } from './saga'

describe('isSagaSettled', () => {
  it('keeps polling while the saga is in flight and stops once it settles', () => {
    // ARRANGE/ACT/ASSERT — table test: this predicate IS the polling lifecycle
    // (orderByIdQuery's refetchInterval returns false exactly when this returns true),
    // so a wrong entry here means either polling forever (battery/network burn) or
    // a timeline frozen at Placed while the backend moves on.
    expect(isSagaSettled('Placed')).toBe(false) // saga just started — keep watching
    expect(isSagaSettled('Paid')).toBe(false) // mid-saga — shipping still to come
    expect(isSagaSettled('Shipped')).toBe(true) // demo happy-path terminal
    expect(isSagaSettled('Delivered')).toBe(true) // post-shipping terminal
    expect(isSagaSettled('Cancelled')).toBe(true) // cancel branch — nothing more will happen
    expect(isSagaSettled('PaymentFailed')).toBe(true) // failure branch — saga stopped
  })
})

describe('deriveStepStates', () => {
  // Timeline is [Placed, Paid, Shipped]; each case pins the full visual contract for a status.
  const cases: [OrderStatus, ReturnType<typeof deriveStepStates>][] = [
    // In-flight: the reached step pulses "active" (page is polling), the rest are pending.
    ['Placed', ['active', 'pending', 'pending']],
    ['Paid', ['complete', 'active', 'pending']],
    // Settled happy path: everything reached is complete — no "active" pulse once polling stops.
    ['Shipped', ['complete', 'complete', 'complete']],
    ['Delivered', ['complete', 'complete', 'complete']],
    // Failure branch: Placed succeeded, the payment step is where the saga died.
    ['PaymentFailed', ['complete', 'failed', 'pending']],
    // Cancelled: terminal but NOT a step failure. The DTO carries only the current status
    // and the backend allows Cancel() from both Placed and Paid, so whether payment ever
    // happened is unknowable client-side — marking Paid as failed would misstate a
    // cancel-before-payment. Remaining steps stay pending; the terminal panel tells the story.
    ['Cancelled', ['complete', 'pending', 'pending']],
  ]

  it.each(cases)('%s renders as %j', (status, expected) => {
    // ACT — derive the per-step visual states from the polled status.
    // ASSERT — the whole array at once: a step silently flipping between
    // active/complete is exactly the regression class this guards.
    expect(deriveStepStates(status)).toEqual(expected)
  })
})
