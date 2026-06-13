import { useQuery } from '@tanstack/react-query'
import { useState, useDeferredValue } from 'react'

import type { Product } from '../types/product'

import { productsQuery, searchProductsQuery } from '../api/products'

import { ProductGrid } from './ProductGrid'

/**
 * Catalog browse + search. The search box drives a deferred value so typing stays
 * responsive while result queries run behind it (frontend/CLAUDE.md "Render & bundle
 * performance" — startTransition/useDeferredValue for non-urgent updates). Search is
 * server-side (the rate-limited /products/search endpoint), not a client filter: the
 * client never sees the full catalog.
 */
export function ProductBrowser({ onAddToCart }: Readonly<{ onAddToCart?: (product: Product) => void }>) {
  const [searchInput, setSearchInput] = useState('')
  const deferredSearch = useDeferredValue(searchInput.trim())
  const searching = deferredSearch.length > 0

  const browse = useQuery(productsQuery())
  const search = useQuery(searchProductsQuery(deferredSearch))

  const active = searching ? search : browse

  return (
    <section className="mx-auto max-w-5xl p-6">
      <div className="mb-6 flex items-center gap-4">
        <h1 className="text-2xl font-semibold">Products</h1>
        <input
          type="search"
          value={searchInput}
          onChange={(e) => {
            setSearchInput(e.target.value)
          }}
          placeholder="Search products…"
          aria-label="Search products"
          className="ml-auto w-72 rounded-md border border-zinc-300 px-3 py-2 text-sm focus:border-zinc-500 focus:outline-none"
        />
      </div>

      {active.isPending && active.fetchStatus !== 'idle' ? (
        <p role="status" className="text-zinc-500">
          Loading products…
        </p>
      ) : active.isError ? (
        <p role="alert" className="text-red-600">
          Couldn&apos;t load products. Please try again.
        </p>
      ) : (
        <ProductGrid
          products={active.data ?? []}
          emptyMessage={searching ? `No products match “${deferredSearch}”.` : 'No products yet.'}
          onAddToCart={onAddToCart}
        />
      )}
    </section>
  )
}
