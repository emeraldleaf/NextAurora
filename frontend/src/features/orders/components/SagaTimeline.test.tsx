import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { SagaTimeline } from './SagaTimeline'

// Pure-render component (polling lives in the query, derivation in saga.ts), so these
// tests assert the user-visible contract per status — RTL by role/label, no internals.
describe('SagaTimeline', () => {
  it('marks the live step and explains the async 202 while the order is Placed', () => {
    // ARRANGE/ACT — freshly placed order: the saga has NOT run yet; this is the state a
    // buyer lands on right after checkout redirects here.
    render(<SagaTimeline status="Placed" />)

    // ASSERT —
    // 1. The Placed step carries aria-current="step": screen readers and tests agree on
    //    which step is live (the pulsing dot alone would be color-only signaling).
    const steps = screen.getAllByRole('listitem')
    expect(steps[0]).toHaveAttribute('aria-current', 'step')
    // 2. The narrator explains the load-bearing backend fact — 202 + outbox — because
    //    "why is my order not confirmed instantly" is exactly what the panel exists to teach.
    expect(screen.getByLabelText('What the backend just did')).toHaveTextContent(/202 Accepted/)
    expect(screen.getByLabelText('What the backend just did')).toHaveTextContent(/transactional outbox/)
    // 3. The polling hint is shown — the saga is still moving.
    expect(screen.getByText(/polls the order every 2/)).toBeInTheDocument()
  })

  it('narrates the payment hop when the order reaches Paid', () => {
    // ARRANGE/ACT — mid-saga: PaymentService has consumed and re-published.
    render(<SagaTimeline status="Paid" />)

    // ASSERT — the narration advances with the saga: now it teaches the consume→publish
    // hop and idempotency (at-least-once delivery), not the 202 story.
    const panel = screen.getByLabelText('What the backend just did')
    expect(panel).toHaveTextContent(/PaymentService consumed OrderPlacedEvent/)
    expect(panel).toHaveTextContent(/idempotent/)
  })

  it('shows a fully-complete timeline with no polling hint once Shipped', () => {
    // ARRANGE/ACT — the saga settled on the happy path.
    render(<SagaTimeline status="Shipped" />)

    // ASSERT —
    // 1. No step is "current" anymore — nothing is in flight.
    for (const step of screen.getAllByRole('listitem')) {
      expect(step).not.toHaveAttribute('aria-current')
    }
    // 2. The final narration teaches the punchline: choreography, no orchestrator.
    expect(screen.getByLabelText('What the backend just did')).toHaveTextContent(/choreography saga/)
    // 3. Polling hint gone — isSagaSettled stopped the refetch loop, the UI says so implicitly.
    expect(screen.queryByText(/polls the order every 2/)).not.toBeInTheDocument()
  })

  it('surfaces the failure branch when payment is declined', () => {
    // ARRANGE/ACT — the saga's compensating path: PaymentFailedEvent came back instead.
    render(<SagaTimeline status="PaymentFailed" />)

    // ASSERT — the failure is loud and explained, not a silent stuck timeline:
    // the panel switches to the terminal narration naming the failure event.
    expect(screen.getByText(/Saga stopped: PaymentFailed/)).toBeInTheDocument()
    expect(screen.getByLabelText('What the backend just did')).toHaveTextContent(/PaymentFailedEvent/)
  })
})
