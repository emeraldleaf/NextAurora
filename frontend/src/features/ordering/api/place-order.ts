import { env } from '@/core/env'
import { postJsonAuthed } from '@/shared/api/http'

import type { CartLine } from '../cart-store'

// Mirror of OrderService PlaceOrderCommand. UnitPrice is sent but the server overrides it
// with the authoritative catalog price — we send it only so the request shape matches the
// command record. BuyerId MUST equal the JWT `sub` or the endpoint returns 403.
interface PlaceOrderCommand {
  buyerId: string
  currency: string
  lines: { productId: string; productName: string; quantity: number; unitPrice: number }[]
}

export interface PlacedOrder {
  id: string
}

export async function placeOrder(
  buyerId: string,
  lines: CartLine[],
  accessToken: string,
): Promise<{ orderId: string; correlationId: string | null }> {
  const currency = lines[0]?.currency ?? 'USD'
  const command: PlaceOrderCommand = {
    buyerId,
    currency,
    lines: lines.map((l) => ({
      productId: l.productId,
      productName: l.productName,
      quantity: l.quantity,
      unitPrice: l.unitPrice,
    })),
  }

  // 202 Accepted — the order row IS the tracking record; the saga proceeds async on the bus.
  const { data, correlationId } = await postJsonAuthed<PlacedOrder>(env.orderApiUrl, '/api/v1/orders', command, accessToken)
  return { orderId: data.id, correlationId }
}
