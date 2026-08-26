import { render, screen, act } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { HOP_DURATION_MS } from '../canvas'

import { SagaCanvas } from './SagaCanvas'

// The canvas replays the order's REAL journey at a readable pace — these tests assert the
// replay reaches the right hops for a given polled status, not pixel positions.
// One act() per hop: the setState from a fired timer flushes at act-end, and only THEN does
// the effect schedule the next hop's timer — a single long advance can't play them all.
function advanceOneHop() {
  act(() => {
    vi.advanceTimersByTime(HOP_DURATION_MS + 50)
  })
}

describe('SagaCanvas', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('renders the real topology under production names', () => {
    // ARRANGE + ACT — a freshly placed order.
    render(<SagaCanvas status="Placed" />)

    // ASSERT — services and the messaging layer are drawn with their real names; the
    // credibility of the canvas rests on nothing being invented.
    expect(screen.getByLabelText('Live saga canvas')).toBeInTheDocument()
    for (const label of ['OrderService', 'PaymentService', 'ShippingService', 'NotificationService']) {
      expect(screen.getByText(label)).toBeInTheDocument()
    }
    expect(screen.getByText('RabbitMQ')).toBeInTheDocument()
    expect(screen.getByText('order-events')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /replay/i })).toBeInTheDocument()
  })

  it('replays a shipped order hop by hop to the choreography punchline', () => {
    // ARRANGE — the saga already settled (real completion takes ~2s, faster than anyone
    // watches); the canvas must still tell the story one hop at a time.
    render(<SagaCanvas status="Shipped" />)

    // ASSERT 1 — the first hop narrates the outbox commit while it plays.
    expect(screen.getByText(/transactional outbox/)).toBeInTheDocument()

    // ACT — let all three hops finish animating, one paced hop at a time.
    advanceOneHop()
    advanceOneHop()
    advanceOneHop()

    // ASSERT 2 — the final caption lands on the architecture punchline.
    expect(screen.getByText(/this is choreography/)).toBeInTheDocument()
  })

  it('plays the failure branch for a PaymentFailed order', () => {
    // ARRANGE — the saga died at payment.
    render(<SagaCanvas status="PaymentFailed" />)

    // ACT — advance past the Placed hop into the failure hop.
    advanceOneHop()

    // ASSERT — the failure event rides the same mechanics, and the caption says so.
    // (The event name renders twice — caption and in-flight badge — hence getAllByText.)
    expect(screen.getAllByText(/PaymentFailedEvent/).length).toBeGreaterThan(0)
    expect(screen.getByText(/outbox \+ fanout mechanics as the happy path/)).toBeInTheDocument()
  })

  it('replay button restarts the story from the first hop', async () => {
    // ARRANGE — a fully-played shipped order.
    render(<SagaCanvas status="Shipped" />)
    advanceOneHop()
    advanceOneHop()
    advanceOneHop()
    expect(screen.getByText(/this is choreography/)).toBeInTheDocument()

    // ACT — replay. (fireEvent via act; userEvent needs real timers.)
    const { fireEvent } = await import('@testing-library/react')
    fireEvent.click(screen.getByRole('button', { name: /replay/i }))

    // ASSERT — back to hop one's narration.
    expect(screen.getByText(/transactional outbox/)).toBeInTheDocument()
    expect(screen.queryByText(/this is choreography/)).not.toBeInTheDocument()
  })
})

describe('SagaCanvas under prefers-reduced-motion', () => {
  it('renders every reached hop immediately — no paced replay to wait through', () => {
    // ARRANGE — a matchMedia stub reporting reduced motion. jsdom has no matchMedia, so
    // the stub also exercises the component's availability guard.
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }))
    try {
      // ACT — a settled order, rendered fresh.
      render(<SagaCanvas status="Shipped" />)

      // ASSERT — the final caption is there at once (no timers), and the replay control
      // is hidden because there is nothing it could meaningfully do.
      expect(screen.getByText(/this is choreography/)).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /replay/i })).not.toBeInTheDocument()
    } finally {
      vi.unstubAllGlobals()
    }
  })
})
