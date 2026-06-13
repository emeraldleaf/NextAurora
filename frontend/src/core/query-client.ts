import { QueryClient } from '@tanstack/react-query'

// Single QueryClient for the app (core/ = singletons). Server state lives HERE, never in
// useState copies — see frontend/CLAUDE.md "Server state". Defaults tuned for a catalog
// that changes rarely: 30s staleness keeps navigation snappy without hiding stock updates
// for long; mutations invalidate their affected queries explicitly in their own onSuccess.
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 2,
    },
  },
})
