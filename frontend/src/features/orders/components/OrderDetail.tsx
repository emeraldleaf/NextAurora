import { useQuery } from '@tanstack/react-query'

import { formatPrice } from '@/shared/format'

import { listenerStatusQuery } from '../api/demo'
import { orderByIdQuery } from '../api/orders'

import { KillSwitchPanel } from './KillSwitchPanel'
import { SagaCanvas } from './SagaCanvas'
import { SagaTimeline } from './SagaTimeline'

/**
 * Single order view — the saga-narrator page (epic #130 Phase 3). orderByIdQuery polls
 * while the saga is in flight, so the timeline below advances live as PaymentService and
 * ShippingService consume and re-publish events; polling stops once the order settles.
 */
export function OrderDetail({ orderId }: Readonly<{ orderId: string }>) {
  const { data, isPending, isError, fetchStatus } = useQuery(orderByIdQuery(orderId))
  // Kill-switch state feeds the canvas (dead node + held-in-queue caption). The query 404s
  // and disables itself outside DemoMode deployments.
  const listener = useQuery(listenerStatusQuery())
  const paymentDown = listener.data != null && listener.data.status !== 'Accepting' && listener.data.status !== 'Unavailable'

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
      {/* key: TanStack Router preserves this component across orderId navigation — a
          finished replay must not leak into the next order's canvas (CodeRabbit, #211). */}
      <SagaCanvas key={data.orderId} status={data.status} paymentDown={paymentDown} />
      <KillSwitchPanel />
      <SagaTimeline status={data.status} />
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
