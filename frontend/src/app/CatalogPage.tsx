import { ProductBrowser } from '@/features/catalog'
import { useCart } from '@/features/ordering'

// App-layer composition: wires catalog's display to the ordering feature's cart. Neither
// feature imports the other — the page is the seam (frontend/CLAUDE.md "Architecture rules").
export function CatalogPage() {
  const add = useCart((s) => s.add)
  return <ProductBrowser onAddToCart={add} />
}
