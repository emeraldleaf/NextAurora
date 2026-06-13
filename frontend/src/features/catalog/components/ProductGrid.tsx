import type { Product } from '../types/product'

const priceFormatters = new Map<string, Intl.NumberFormat>()

function formatPrice(price: number, currency: string): string {
  let fmt = priceFormatters.get(currency)
  if (!fmt) {
    fmt = new Intl.NumberFormat(undefined, { style: 'currency', currency })
    priceFormatters.set(currency, fmt)
  }
  return fmt.format(price)
}

export function ProductGrid({ products, emptyMessage }: { products: Product[]; emptyMessage: string }) {
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
        </li>
      ))}
    </ul>
  )
}
