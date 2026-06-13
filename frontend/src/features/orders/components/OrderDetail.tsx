import { useQuery } from '@tanstack/react-query'

import { formatPrice } from '@/shared/format'

import { orderByIdQuery } from '../api/orders'

/**
 * Single order view. Phase 3 turns the status line into the saga timeline + narrator
 * (Placed → Paid → Shipped with "what the backend just did" panels) and polls this query;
 * for now it's a static read of current status.
 */
export function OrderDetail({ orderId }: Readonly<{ orderId: string }>) {
  const { data, isPending, isError, fetchStatus } = useQuery(orderByIdQuery(orderId))

  if (isPending && fetchStatus !== 'idle') {
    return (
      <p role="status" className="text-zinc-500">
        Loading order…
      </p>
    )
  }
  if (isError || data == null) {
    return (
      <p role="alert" className="text-red-600">
        Order not found.
      </p>
    )
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">Order {data.orderId.slice(0, 8)}</h1>
        <span className="rounded-full bg-zinc-100 px-2 py-0.5 text-xs">{data.status}</span>
      </div>
      <ul className="divide-y divide-zinc-200">
        {data.lines.map((line) => (
          <li key={line.productId} className="flex items-center gap-3 py-2 text-sm">
            <span className="flex-1">{line.productName}</span>
            <span className="text-zinc-500">×{line.quantity}</span>
            <span className="w-20 text-right">{formatPrice(line.unitPrice * line.quantity, data.currency)}</span>
          </li>
        ))}
      </ul>
      <div className="flex justify-between border-t border-zinc-200 pt-3 font-semibold">
        <span>Total</span>
        <span>{formatPrice(data.totalAmount, data.currency)}</span>
      </div>
    </div>
  )
}
