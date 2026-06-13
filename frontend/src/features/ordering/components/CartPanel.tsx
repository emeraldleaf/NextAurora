import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'

import { userManager } from '@/core/auth'
import { useAuth } from '@/features/auth'
import { formatPrice } from '@/shared/format'

import { placeOrder } from '../api/place-order'
import { useCart, selectSubtotal } from '../cart-store'

/**
 * Cart + checkout. Placing the order is a user action (a mutation), so it lives in an
 * event handler via useMutation — never an effect (frontend/CLAUDE.md "Effects discipline").
 * On success we invalidate the buyer's orders query so "My Orders" reflects the new order
 * without a manual refetch (the write-path-invalidates rule).
 */
export function CartPanel() {
  const lines = useCart((s) => s.lines)
  const subtotal = useCart(selectSubtotal)
  const setQuantity = useCart((s) => s.setQuantity)
  const clear = useCart((s) => s.clear)
  const { isAuthenticated, buyerId, login } = useAuth()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const checkout = useMutation({
    mutationFn: async () => {
      // Access token is read at submit time, not held in component state — it can refresh.
      const user = await userManager.getUser()
      if (user == null || buyerId == null) throw new Error('Not signed in')
      return placeOrder(buyerId, lines, user.access_token)
    },
    onSuccess: async ({ orderId }) => {
      clear()
      if (buyerId != null) {
        await queryClient.invalidateQueries({ queryKey: ['orders', 'byBuyer', buyerId] })
      }
      await navigate({ to: '/orders/$orderId', params: { orderId } })
    },
  })

  if (lines.length === 0) {
    return <p className="text-zinc-500">Your cart is empty.</p>
  }

  return (
    <div className="space-y-4">
      <ul className="divide-y divide-zinc-200">
        {lines.map((line) => (
          <li key={line.productId} className="flex items-center gap-3 py-3">
            <span className="flex-1 text-sm">{line.productName}</span>
            <input
              type="number"
              min={0}
              value={line.quantity}
              aria-label={`Quantity for ${line.productName}`}
              onChange={(e) => {
                setQuantity(line.productId, Number(e.target.value))
              }}
              className="w-16 rounded border border-zinc-300 px-2 py-1 text-sm"
            />
            <span className="w-20 text-right text-sm">{formatPrice(line.unitPrice * line.quantity, line.currency)}</span>
          </li>
        ))}
      </ul>

      <div className="flex items-center justify-between border-t border-zinc-200 pt-3">
        <span className="text-sm text-zinc-500">Subtotal (server confirms final price)</span>
        <span className="font-semibold">{formatPrice(subtotal, lines[0]?.currency ?? 'USD')}</span>
      </div>

      {isAuthenticated ? (
        <button
          type="button"
          onClick={() => {
            checkout.mutate()
          }}
          disabled={checkout.isPending}
          className="w-full rounded-md bg-zinc-900 px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
        >
          {checkout.isPending ? 'Placing order…' : 'Place order'}
        </button>
      ) : (
        <button
          type="button"
          onClick={() => {
            void login()
          }}
          className="w-full rounded-md border border-zinc-900 px-4 py-2 text-sm font-medium"
        >
          Sign in to check out
        </button>
      )}

      {checkout.isError ? (
        <p role="alert" className="text-sm text-red-600">
          Couldn&apos;t place the order. Please try again.
        </p>
      ) : null}
    </div>
  )
}
