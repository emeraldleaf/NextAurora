import { create } from 'zustand'

import type { Product } from '@/features/catalog'

export interface CartLine {
  productId: string
  productName: string
  unitPrice: number
  currency: string
  quantity: number
}

interface CartState {
  lines: CartLine[]
  add: (product: Product) => void
  setQuantity: (productId: string, quantity: number) => void
  remove: (productId: string) => void
  clear: () => void
}

// Cart is client-only UI state (Zustand, the one sanctioned small global per
// frontend/CLAUDE.md "Client state") — it is NOT server state, so it does not belong in
// TanStack Query. Note: unitPrice here is for DISPLAY only. The server recomputes the
// authoritative price from the catalog during placement (CLAUDE.md "Server-controlled
// fields") — the cart total is a preview, never the source of truth.
export const useCart = create<CartState>((set) => ({
  lines: [],
  add: (product) => {
    set((state) => {
      const existing = state.lines.find((l) => l.productId === product.id)
      if (existing) {
        return {
          lines: state.lines.map((l) => (l.productId === product.id ? { ...l, quantity: l.quantity + 1 } : l)),
        }
      }
      return {
        lines: [
          ...state.lines,
          {
            productId: product.id,
            productName: product.name,
            unitPrice: product.price,
            currency: product.currency,
            quantity: 1,
          },
        ],
      }
    })
  },
  setQuantity: (productId, quantity) => {
    set((state) => ({
      lines:
        quantity <= 0
          ? state.lines.filter((l) => l.productId !== productId)
          : state.lines.map((l) => (l.productId === productId ? { ...l, quantity } : l)),
    }))
  },
  remove: (productId) => {
    set((state) => ({ lines: state.lines.filter((l) => l.productId !== productId) }))
  },
  clear: () => {
    set({ lines: [] })
  },
}))

// Derived selectors — computed from state, not stored (frontend/CLAUDE.md: derive, don't duplicate).
export const selectItemCount = (s: CartState) => s.lines.reduce((n, l) => n + l.quantity, 0)
export const selectSubtotal = (s: CartState) => s.lines.reduce((sum, l) => sum + l.unitPrice * l.quantity, 0)
