import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'

import { useAuth } from '@/features/auth'
import { formatPrice } from '@/shared/format'

import { ordersByBuyerQuery } from '../api/orders'

/** "My Orders" — buyer-scoped list. The backend filters by JWT `sub` in SQL, so this only
 *  ever returns the signed-in buyer's own orders (the IDOR-safe read). */
export function OrderList() {
  const { buyerId, isAuthenticated } = useAuth()
  const { data, isPending, isError, fetchStatus } = useQuery(ordersByBuyerQuery(buyerId))

  if (!isAuthenticated) {
    return <p className="text-zinc-500">Sign in to see your orders.</p>
  }
  if (isPending && fetchStatus !== 'idle') {
    return (
      <p role="status" className="text-zinc-500">
        Loading your orders…
      </p>
    )
  }
  if (isError) {
    return (
      <p role="alert" className="text-red-600">
        Couldn&apos;t load your orders. Please try again.
      </p>
    )
  }
  if ((data ?? []).length === 0) {
    return <p className="text-zinc-500">You haven&apos;t placed any orders yet.</p>
  }

  return (
    <ul className="divide-y divide-zinc-200">
      {data?.map((order) => (
        <li key={order.orderId}>
          <Link to="/orders/$orderId" params={{ orderId: order.orderId }} className="flex items-center gap-4 py-3 hover:bg-zinc-50">
            <span className="font-mono text-xs text-zinc-500">{order.orderId.slice(0, 8)}</span>
            <span className="rounded-full bg-zinc-100 px-2 py-0.5 text-xs">{order.status}</span>
            <span className="ml-auto text-sm font-medium">{formatPrice(order.totalAmount, order.currency)}</span>
          </Link>
        </li>
      ))}
    </ul>
  )
}
