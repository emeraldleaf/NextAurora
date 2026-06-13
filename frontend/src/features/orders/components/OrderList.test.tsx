import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider, createRootRoute, createRouter, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { env } from '@/core/env'
import { AuthContext, type AuthState } from '@/features/auth/auth-context'
import { server } from '@/test/server'

import { OrderList } from './OrderList'

// The orders query reads the access token from userManager; stub it so no real OIDC runs.
vi.mock('@/core/auth', () => ({
  userManager: { getUser: () => Promise.resolve({ access_token: 'test-token' }) },
}))

const buyerId = '11111111-1111-1111-1111-111111111111'

function authValue(overrides: Partial<AuthState> = {}): AuthState {
  return {
    user: null,
    isAuthenticated: true,
    isLoading: false,
    buyerId,
    login: () => Promise.resolve(),
    logout: () => Promise.resolve(),
    ...overrides,
  }
}

// Render OrderList inside a minimal router (it uses <Link>) + query client + auth context.
function renderOrders(auth: AuthState) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const rootRoute = createRootRoute({ component: OrderList })
  const router = createRouter({
    routeTree: rootRoute,
    history: createMemoryHistory({ initialEntries: ['/'] }),
  })
  return render(
    <QueryClientProvider client={client}>
      <AuthContext value={auth}>
        <RouterProvider router={router} />
      </AuthContext>
    </QueryClientProvider>,
  )
}

describe('OrderList', () => {
  beforeEach(() => {
    server.resetHandlers()
  })

  it('renders the signed-in buyer’s orders', async () => {
    // ARRANGE — backend returns OrderSummary[] for this buyer only (IDOR-safe server-side).
    server.use(
      http.get(`${env.orderApiUrl}/api/v1/orders/buyer/${buyerId}`, () =>
        HttpResponse.json([
          { orderId: 'aaaa1111-0000-0000-0000-000000000000', buyerId, status: 'Placed', totalAmount: 39.98, currency: 'USD', placedAt: '2026-06-12T00:00:00Z', lines: [] },
        ]),
      ),
    )

    // ACT
    renderOrders(authValue())

    // ASSERT — status badge + formatted total render.
    expect(await screen.findByText('Placed')).toBeInTheDocument()
    expect(screen.getByText('$39.98')).toBeInTheDocument()
  })

  it('prompts to sign in when unauthenticated (no request made)', async () => {
    // ARRANGE — no MSW handler registered; if the component fetched, server.listen's
    // onUnhandledRequest:'error' would fail the test. So this also proves the query is
    // disabled when there is no buyer.
    renderOrders(authValue({ isAuthenticated: false, buyerId: null }))

    // ASSERT — findBy waits for the router to mount the component.
    expect(await screen.findByText(/sign in to see your orders/i)).toBeInTheDocument()
  })

  it('shows the empty state for a buyer with no orders', async () => {
    server.use(http.get(`${env.orderApiUrl}/api/v1/orders/buyer/${buyerId}`, () => HttpResponse.json([])))
    renderOrders(authValue())
    expect(await screen.findByText(/haven.t placed any orders/i)).toBeInTheDocument()
  })
})
