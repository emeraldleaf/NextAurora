import { queryOptions } from '@tanstack/react-query'

import { env } from '@/core/env'
import { getJson } from '@/shared/api/http'

import type { Product } from '../types/product'

// Query-key factory per frontend/CLAUDE.md "Server state": [feature, entity, params].
// All catalog reads flow through these queryOptions — components never call fetch.
export const catalogKeys = {
  products: (page: number) => ['catalog', 'products', { page }] as const,
  search: (query: string, page: number) => ['catalog', 'search', { query, page }] as const,
}

export function productsQuery(page = 1) {
  return queryOptions({
    queryKey: catalogKeys.products(page),
    queryFn: async ({ signal }) => {
      const { data } = await getJson<Product[]>(env.catalogApiUrl, `/api/v1/products?page=${String(page)}&pageSize=50`, signal)
      return data
    },
  })
}

export function searchProductsQuery(query: string, page = 1) {
  return queryOptions({
    queryKey: catalogKeys.search(query, page),
    queryFn: async ({ signal }) => {
      const { data } = await getJson<Product[]>(
        env.catalogApiUrl,
        `/api/v1/products/search?query=${encodeURIComponent(query)}&page=${String(page)}&pageSize=50`,
        signal,
      )
      return data
    },
    enabled: query.trim().length > 0,
  })
}
