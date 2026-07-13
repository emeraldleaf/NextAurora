import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'

import { env } from '@/core/env'
import { server } from '@/test/server'

import type { CartLine } from '../cart-store'

import { placeOrder } from './place-order'

const lines: CartLine[] = [{ productId: 'p1', productName: 'Aurora Lamp', unitPrice: 10, currency: 'USD', quantity: 2 }]

describe('placeOrder', () => {
  it('POSTs the command with a bearer token and returns the 202 order id', async () => {
    // ARRANGE — capture what the client actually sends. The backend contract: BuyerId in
    // body must equal the JWT sub (enforced server-side); Authorization is a bearer token;
    // 202 Accepted returns { id }.
    let authHeader: string | null = null
    let body: unknown = null
    server.use(
      http.post(`${env.orderApiUrl}/api/v1/orders`, async ({ request }) => {
        authHeader = request.headers.get('Authorization')
        body = await request.json()
        return HttpResponse.json({ id: 'order-123' }, { status: 202, headers: { 'X-Correlation-Id': 'corr-xyz' } })
      }),
    )

    // ACT
    const result = await placeOrder('buyer-1', lines, 'test-access-token')

    // ASSERT — four invariants:
    //  1) returns the server-assigned order id
    //  2) surfaces the correlation id (for the observability UI)
    //  3) sends the bearer token
    //  4) sends BuyerId + line shape matching PlaceOrderCommand
    expect(result.orderId).toBe('order-123')
    expect(result.correlationId).toBe('corr-xyz')
    expect(authHeader).toBe('Bearer test-access-token')
    expect(body).toMatchObject({
      buyerId: 'buyer-1',
      currency: 'USD',
      lines: [{ productId: 'p1', quantity: 2 }],
    })
  })

  it('throws ApiError on a 403 (buyer mismatch)', async () => {
    // ARRANGE — server rejects when BuyerId != JWT sub. The client must surface a failure,
    // not silently swallow it (the CartPanel renders the error state off this throw).
    server.use(http.post(`${env.orderApiUrl}/api/v1/orders`, () => HttpResponse.json(null, { status: 403 })))

    // ACT / ASSERT
    await expect(placeOrder('buyer-1', lines, 'bad-token')).rejects.toThrow()
  })
})
