import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router'

import { queryClient } from '@/core/query-client'
import { AuthCallback } from '@/features/auth'
import { productsQuery } from '@/features/catalog'
import { CartPanel } from '@/features/ordering'
import { OrderDetail, OrderList } from '@/features/orders'

import { CatalogPage } from './CatalogPage'
import { Layout } from './Layout'

// Code-based routes (the file-based plugin earns its keep at more routes than this).
// Loaders prefetch into the query cache so navigation never waterfalls render → fetch —
// frontend/CLAUDE.md "No request waterfalls on the critical path".
const rootRoute = createRootRoute({ component: Layout })

const catalogRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  loader: () => queryClient.ensureQueryData(productsQuery()),
  component: CatalogPage,
})

const cartRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/cart',
  component: () => (
    <section className="mx-auto max-w-2xl p-6">
      <h1 className="mb-6 text-2xl font-semibold">Cart</h1>
      <CartPanel />
    </section>
  ),
})

const ordersRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/orders',
  component: () => (
    <section className="mx-auto max-w-2xl p-6">
      <h1 className="mb-6 text-2xl font-semibold">My Orders</h1>
      <OrderList />
    </section>
  ),
})

const orderDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  // No loader: order reads require a bearer token, so fetching is left to the component's
  // useQuery (which renders auth/error states) rather than a loader that throws pre-render.
  path: '/orders/$orderId',
  component: function OrderDetailRoute() {
    const { orderId } = orderDetailRoute.useParams()
    return (
      <section className="mx-auto max-w-2xl p-6">
        <OrderDetail orderId={orderId} />
      </section>
    )
  },
})

const authCallbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/auth/callback',
  component: AuthCallback,
})

const routeTree = rootRoute.addChildren([catalogRoute, cartRoute, ordersRoute, orderDetailRoute, authCallbackRoute])

export const router = createRouter({ routeTree })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}
