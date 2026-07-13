import { Link, Outlet } from '@tanstack/react-router'

import { useAuth } from '@/features/auth'
import { selectItemCount, useCart } from '@/features/ordering'

// App shell: nav with cart count + auth state. The header reads the cart count via a
// derived selector (computed, not stored) and the auth state from context.
export function Layout() {
  const itemCount = useCart(selectItemCount)
  const { isAuthenticated, isLoading, login, logout, user } = useAuth()

  return (
    <div className="min-h-screen bg-zinc-50 text-zinc-900">
      <header className="flex items-center gap-4 border-b border-zinc-200 bg-white px-6 py-3">
        <Link to="/" className="font-semibold">
          NextAurora
        </Link>
        <nav className="flex items-center gap-4 text-sm">
          <Link to="/" className="text-zinc-600 hover:text-zinc-900">
            Catalog
          </Link>
          <Link to="/cart" className="text-zinc-600 hover:text-zinc-900">
            Cart{itemCount > 0 ? ` (${String(itemCount)})` : ''}
          </Link>
          <Link to="/orders" className="text-zinc-600 hover:text-zinc-900">
            My Orders
          </Link>
        </nav>
        <div className="ml-auto text-sm">
          {isLoading ? null : isAuthenticated ? (
            <span className="flex items-center gap-3">
              <span className="text-zinc-500">{user?.profile.preferred_username ?? 'signed in'}</span>
              <button type="button" onClick={() => void logout()} className="text-zinc-600 hover:text-zinc-900">
                Sign out
              </button>
            </span>
          ) : (
            <button type="button" onClick={() => void login()} className="font-medium text-zinc-900">
              Sign in
            </button>
          )}
        </div>
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  )
}
