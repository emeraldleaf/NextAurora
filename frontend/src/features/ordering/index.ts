// Public API of the ordering feature. See frontend/CLAUDE.md "Architecture rules".
export { CartPanel } from './components/CartPanel'
export { useCart, selectItemCount, selectSubtotal } from './cart-store'
export type { CartLine } from './cart-store'
