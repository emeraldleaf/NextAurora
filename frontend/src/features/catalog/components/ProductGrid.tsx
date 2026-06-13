import { formatPrice } from '@/shared/format'

import type { Product } from '../types/product'

// Catalog stays a pure display feature — it knows nothing about the cart. The page wires
// `onAddToCart` to the ordering feature, so catalog has zero dependency on ordering
// (frontend/CLAUDE.md "Architecture rules": features don't reach into each other).
interface ProductGridProps {
  products: Product[]
  emptyMessage: string
  onAddToCart?: (product: Product) => void
}

export function ProductGrid({ products, emptyMessage, onAddToCart }: Readonly<ProductGridProps>) {
  if (products.length === 0) {
    return <p className="text-zinc-500">{emptyMessage}</p>
  }

  return (
    <ul className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {products.map((product) => (
        <li key={product.id} className="rounded-lg border border-zinc-200 p-4 shadow-sm">
          <div className="flex items-baseline justify-between gap-2">
            <h2 className="font-medium">{product.name}</h2>
            <span className="whitespace-nowrap text-sm font-semibold">{formatPrice(product.price, product.currency)}</span>
          </div>
          <p className="mt-1 line-clamp-2 text-sm text-zinc-600">{product.description}</p>
          <p className="mt-2 text-xs text-zinc-500">
            {product.category} ·{' '}
            {product.isAvailable ? `${String(product.stockQuantity)} in stock` : <span className="text-red-600">unavailable</span>}
          </p>
          {onAddToCart && product.isAvailable ? (
            <button
              type="button"
              onClick={() => {
                onAddToCart(product)
              }}
              className="mt-3 w-full rounded-md border border-zinc-300 px-3 py-1.5 text-sm font-medium hover:bg-zinc-100"
            >
              Add to cart
            </button>
          ) : null}
        </li>
      ))}
    </ul>
  )
}
