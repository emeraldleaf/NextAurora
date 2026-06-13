import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { env } from '@/core/env'
import { server } from '@/test/server'

import type { Product } from '../types/product'

import { ProductBrowser } from './ProductBrowser'

// Fresh QueryClient per test — retries off so error-path tests fail fast instead of
// exercising TanStack Query's backoff, and no cache bleeds between tests.
function renderBrowser() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ProductBrowser />
    </QueryClientProvider>,
  )
}

function product(overrides: Partial<Product>): Product {
  return {
    id: crypto.randomUUID(),
    name: 'Test Product',
    description: 'A product seeded by tests',
    price: 19.99,
    currency: 'USD',
    category: 'Test',
    sellerId: 'test-seller',
    stockQuantity: 10,
    isAvailable: true,
    ...overrides,
  }
}

describe('ProductBrowser', () => {
  it('renders products from the catalog API', async () => {
    // ARRANGE — the backend contract: GET /api/v1/products returns ProductDto[].
    server.use(
      http.get(`${env.catalogApiUrl}/api/v1/products`, () =>
        HttpResponse.json([product({ name: 'Aurora Lamp', price: 49.5 }), product({ name: 'Nova Mug' })]),
      ),
    )

    // ACT
    renderBrowser()

    // ASSERT — both products visible with formatted price; loading state resolved.
    expect(await screen.findByText('Aurora Lamp')).toBeInTheDocument()
    expect(screen.getByText('Nova Mug')).toBeInTheDocument()
    expect(screen.getByText('$49.50')).toBeInTheDocument()
  })

  it('shows the empty state when the catalog has no products', async () => {
    // ARRANGE — empty catalog is a normal state, not an error.
    server.use(http.get(`${env.catalogApiUrl}/api/v1/products`, () => HttpResponse.json([])))

    // ACT
    renderBrowser()

    // ASSERT
    expect(await screen.findByText('No products yet.')).toBeInTheDocument()
  })

  it('shows the error state when the catalog API fails', async () => {
    // ARRANGE — 500 from the service. The UI must show a generic retryable message,
    // never the raw error (backend never leaks internals; neither does the UI).
    server.use(http.get(`${env.catalogApiUrl}/api/v1/products`, () => HttpResponse.json(null, { status: 500 })))

    // ACT
    renderBrowser()

    // ASSERT — role="alert" so it's announced; message is generic.
    expect(await screen.findByRole('alert')).toHaveTextContent(/couldn.t load products/i)
  })

  it('searches server-side and renders matches', async () => {
    // ARRANGE — browse returns one set; search returns a different set. Asserting the
    // search RESULT proves the query hit the server (client-side filtering of the browse
    // payload could never produce "Quantum Kettle").
    server.use(
      http.get(`${env.catalogApiUrl}/api/v1/products`, () => HttpResponse.json([product({ name: 'Aurora Lamp' })])),
      http.get(`${env.catalogApiUrl}/api/v1/products/search`, ({ request }) => {
        const query = new URL(request.url).searchParams.get('query')
        return query === 'kettle' ? HttpResponse.json([product({ name: 'Quantum Kettle' })]) : HttpResponse.json([])
      }),
    )

    // ACT — type into the search box.
    renderBrowser()
    await screen.findByText('Aurora Lamp')
    await userEvent.type(screen.getByRole('searchbox', { name: /search products/i }), 'kettle')

    // ASSERT — server-provided match renders; browse content is replaced.
    expect(await screen.findByText('Quantum Kettle')).toBeInTheDocument()
    expect(screen.queryByText('Aurora Lamp')).not.toBeInTheDocument()
  })

  it('shows a search-specific empty state for no matches', async () => {
    // ARRANGE
    server.use(
      http.get(`${env.catalogApiUrl}/api/v1/products`, () => HttpResponse.json([product({ name: 'Aurora Lamp' })])),
      http.get(`${env.catalogApiUrl}/api/v1/products/search`, () => HttpResponse.json([])),
    )

    // ACT
    renderBrowser()
    await screen.findByText('Aurora Lamp')
    await userEvent.type(screen.getByRole('searchbox', { name: /search products/i }), 'zzz')

    // ASSERT — the message names the query so the user knows what produced zero results.
    expect(await screen.findByText(/no products match/i)).toBeInTheDocument()
  })
})
