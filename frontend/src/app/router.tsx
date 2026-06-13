import { createRootRoute, createRoute, createRouter, Outlet } from '@tanstack/react-router'

import { queryClient } from '@/core/query-client'
import { ProductBrowser, productsQuery } from '@/features/catalog'

// Code-based routes (the file-based plugin earns its keep at more routes than this).
// Loaders prefetch into the query cache so navigation never waterfalls render → fetch —
// frontend/CLAUDE.md "No request waterfalls on the critical path".
const rootRoute = createRootRoute({
  component: () => (
    <div className="min-h-screen bg-zinc-50 text-zinc-900">
      <header className="border-b border-zinc-200 bg-white px-6 py-3">
        <span className="font-semibold">NextAurora</span>
      </header>
      <Outlet />
    </div>
  ),
})

const catalogRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  loader: () => queryClient.ensureQueryData(productsQuery()),
  component: ProductBrowser,
})

const routeTree = rootRoute.addChildren([catalogRoute])

export const router = createRouter({ routeTree })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
