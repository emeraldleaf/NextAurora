// Public API of the catalog feature — everything else is private. See frontend/CLAUDE.md
// "Architecture rules": cross-feature imports go through this file only (ESLint-enforced).
export { ProductBrowser } from './components/ProductBrowser'
export { productsQuery, searchProductsQuery, catalogKeys } from './api/products'
export type { Product } from './types/product'
