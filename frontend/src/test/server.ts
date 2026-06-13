import { setupServer } from 'msw/node'

// MSW intercepts at the network boundary — tests assert what the user sees against
// realistic wire responses, never mock hooks or components. Handlers are registered
// per-test via server.use(...) so each test states its own backend contract.
export const server = setupServer()
