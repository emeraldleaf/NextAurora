import { queryOptions } from '@tanstack/react-query'

import { userManager } from '@/core/auth'
import { env } from '@/core/env'
import { getJsonAuthed } from '@/shared/api/http'

import { isSagaSettled, type OrderStatus } from '../saga'

export interface OrderLineSummary {
  productId: string
  productName: string
  quantity: number
  unitPrice: number
}

export interface OrderSummary {
  orderId: string
  buyerId: string
  status: OrderStatus
  totalAmount: number
  currency: string
  placedAt: string
  lines: OrderLineSummary[]
}

/** Poll cadence while the saga is in flight — it completes in seconds on the live stack. */
export const SAGA_POLL_INTERVAL_MS = 2000

// Query-key factory per frontend/CLAUDE.md "Server state": [feature, entity, params].
export const orderKeys = {
  byBuyer: (buyerId: string) => ['orders', 'byBuyer', buyerId] as const,
  byId: (orderId: string) => ['orders', 'byId', orderId] as const,
}

async function token(): Promise<string> {
  const user = await userManager.getUser()
  if (user == null) throw new Error('Not signed in')
  return user.access_token
}

export function ordersByBuyerQuery(buyerId: string | null) {
  return queryOptions({
    queryKey: orderKeys.byBuyer(buyerId ?? 'anonymous'),
    queryFn: async ({ signal }) => {
      const { data } = await getJsonAuthed<OrderSummary[]>(env.orderApiUrl, `/api/v1/orders/buyer/${buyerId ?? ''}`, await token(), signal)
      return data
    },
    enabled: buyerId != null,
  })
}

export function orderByIdQuery(orderId: string) {
  return queryOptions({
    queryKey: orderKeys.byId(orderId),
    queryFn: async ({ signal }) => {
      const { data } = await getJsonAuthed<OrderSummary>(env.orderApiUrl, `/api/v1/orders/${orderId}`, await token(), signal)
      return data
    },
    // The saga narrator: poll while the order is still moving through the saga
    // (Placed/Paid), stop the moment it settles (Shipped/Delivered/Cancelled/
    // PaymentFailed). Polling lives HERE, in the query definition, so every consumer
    // of this query gets the same lifecycle — no component-level timers.
    refetchInterval: (query) => {
      const status = query.state.data?.status
      if (status == null || isSagaSettled(status)) return false
      return SAGA_POLL_INTERVAL_MS
    },
  })
}
