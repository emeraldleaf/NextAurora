import { beforeEach, describe, expect, it } from 'vitest'

import type { Product } from '@/features/catalog'

import { useCart, selectItemCount, selectSubtotal } from './cart-store'

function product(overrides: Partial<Product> = {}): Product {
  return {
    id: 'p1',
    name: 'Aurora Lamp',
    description: '',
    price: 10,
    currency: 'USD',
    category: 'Home',
    sellerId: 's1',
    stockQuantity: 5,
    isAvailable: true,
    ...overrides,
  }
}

describe('cart-store', () => {
  beforeEach(() => {
    useCart.getState().clear()
  })

  it('adds a product as a new line, then increments quantity on re-add', () => {
    // ARRANGE/ACT — add the same product twice.
    const lamp = product()
    useCart.getState().add(lamp)
    useCart.getState().add(lamp)

    // ASSERT — one line, quantity 2 (re-adding does NOT create a duplicate line). This is
    // the invariant a naive push() would break.
    const { lines } = useCart.getState()
    expect(lines).toHaveLength(1)
    expect(lines[0]?.quantity).toBe(2)
  })

  it('setQuantity to 0 removes the line (quantity is never negative/zero in cart)', () => {
    useCart.getState().add(product())
    useCart.getState().setQuantity('p1', 0)
    expect(useCart.getState().lines).toHaveLength(0)
  })

  it('subtotal and item count derive from lines (computed, not stored)', () => {
    // ARRANGE — two products, mixed quantities.
    useCart.getState().add(product({ id: 'p1', price: 10 }))
    useCart.getState().add(product({ id: 'p2', price: 25 }))
    useCart.getState().setQuantity('p1', 3) // 3 × $10

    // ASSERT — selectors compute from state: count = 3+1 = 4, subtotal = 30+25 = 55.
    const s = useCart.getState()
    expect(selectItemCount(s)).toBe(4)
    expect(selectSubtotal(s)).toBe(55)
  })
})
