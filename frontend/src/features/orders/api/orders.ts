import { queryOptions } from '@tanstack/react-query'

import { userManager } from '@/core/auth'
import { env } from '@/core/env'
import { getJsonAuthed } from '@/shared/api/http'

export interface OrderLineSummary {
  productId: string
  productName: string
  quantity: number
  unitPrice: number
}

export interface OrderSummary {
  orderId: string
  buyerId: string
  status: string
  totalAmount: number
  currency: string
  placedAt: string
  lines: OrderLineSummary[]
}

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
  })
}
