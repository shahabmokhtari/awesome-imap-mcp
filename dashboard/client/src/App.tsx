import { useState, useCallback } from 'react'
import { Routes, Route, NavLink, Navigate } from 'react-router-dom'
import { useAccounts, useAuthStatus } from './hooks/useApi'
import { useQueryClient } from '@tanstack/react-query'
import Overview from './pages/Overview'
import Accounts from './pages/Accounts'
import Sync from './pages/Sync'
import Queue from './pages/Queue'
import Settings from './pages/Settings'
import SetupWizard from './pages/SetupWizard'
import OAuthCallback from './pages/OAuthCallback'
import PinLogin from './pages/PinLogin'
import Messages from './pages/Messages'
import Logs from './pages/Logs'
import Tools from './pages/Tools'

const navItems = [
  { to: '/', label: 'Overview' },
  { to: '/accounts', label: 'Accounts' },
  { to: '/messages', label: 'Messages' },
  { to: '/sync', label: 'Sync' },
  { to: '/queue', label: 'Queue' },
  { to: '/logs', label: 'Logs' },
  { to: '/tools', label: 'Tools' },
  { to: '/settings', label: 'Settings' },
]

function MainLayout({ onLogout }: { onLogout?: () => void }) {
  const handleLogout = async () => {
    const token = localStorage.getItem('dashboard_token')
    if (token) {
      await fetch('/api/auth/logout', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
      }).catch((err) => console.warn('Logout API failed:', err))
    }
    localStorage.removeItem('dashboard_token')
    onLogout?.()
  }

  return (
    <div className="flex h-screen bg-gray-50">
      {/* Sidebar */}
      <nav className="w-56 bg-gray-900 text-gray-300 flex flex-col">
        <div className="px-4 py-5 border-b border-gray-700">
          <h1 className="text-lg font-semibold text-white">IMAP Dashboard</h1>
        </div>
        <ul className="flex-1 py-4 space-y-1">
          {navItems.map((item) => (
            <li key={item.to}>
              <NavLink
                to={item.to}
                end={item.to === '/'}
                className={({ isActive }) =>
                  `block px-4 py-2 text-sm transition-colors ${
                    isActive
                      ? 'bg-gray-800 text-white border-l-2 border-blue-500'
                      : 'hover:bg-gray-800 hover:text-white'
                  }`
                }
              >
                {item.label}
              </NavLink>
            </li>
          ))}
        </ul>
        <div className="px-4 py-3 border-t border-gray-700">
          {onLogout && (
            <button
              onClick={handleLogout}
              className="block w-full text-left text-xs text-gray-400 hover:text-white transition-colors mb-2"
            >
              Logout
            </button>
          )}
          <div className="text-xs text-gray-500">ultimate-imap-mcp v0.1.0</div>
        </div>
      </nav>

      {/* Main content */}
      <main className="flex-1 overflow-auto p-6">
        <Routes>
          <Route path="/" element={<Overview />} />
          <Route path="/accounts" element={<Accounts />} />
          <Route path="/messages" element={<Messages />} />
          <Route path="/sync" element={<Sync />} />
          <Route path="/queue" element={<Queue />} />
          <Route path="/logs" element={<Logs />} />
          <Route path="/tools" element={<Tools />} />
          <Route path="/settings" element={<Settings />} />
        </Routes>
      </main>
    </div>
  )
}

/**
 * Guard component that handles:
 * 1. PIN auth — if PIN is set and no valid token, show login
 * 2. First-run — if no accounts and no setup_completed, redirect to wizard
 * 3. Otherwise — render main layout
 */
function SetupGuard() {
  const [authed, setAuthed] = useState(() => !!localStorage.getItem('dashboard_token'))
  const { data: authStatus, isLoading: authLoading } = useAuthStatus()
  const { data: accounts, isLoading: accountsLoading, error: accountsError } = useAccounts()
  const queryClient = useQueryClient()

  const handleLoginSuccess = useCallback(() => {
    setAuthed(true)
    queryClient.invalidateQueries() // Refetch everything after login
  }, [queryClient])

  const handleLogout = useCallback(() => {
    setAuthed(false)
    queryClient.clear()
  }, [queryClient])

  // Show loading only briefly while checking auth status
  if (authLoading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-400 text-sm">Loading...</div>
      </div>
    )
  }

  const pinIsSet = authStatus?.hasPinSet === true

  // If PIN is set and not authenticated, show login
  if (pinIsSet && !authed) {
    return <PinLogin onSuccess={handleLoginSuccess} />
  }

  // If accounts request failed (likely 401 from stale token), clear token and show login
  if (pinIsSet && accountsError) {
    localStorage.removeItem('dashboard_token')
    return <PinLogin onSuccess={handleLoginSuccess} />
  }

  // If accounts are still loading, wait
  if (accountsLoading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-400 text-sm">Loading...</div>
      </div>
    )
  }

  // First-time user with no accounts -- redirect to wizard
  const hasAccounts = accounts && accounts.length > 0
  const hasCompletedSetup = localStorage.getItem('setup_completed') === 'true'
  if (!hasAccounts && !hasCompletedSetup) {
    return <Navigate to="/setup" replace />
  }

  const showLogout = authStatus?.hasPinSet === true
  return <MainLayout onLogout={showLogout ? handleLogout : undefined} />
}

export default function App() {
  return (
    <Routes>
      {/* Setup wizard is always accessible at /setup (full-screen, no sidebar) */}
      <Route path="/setup" element={<SetupWizard />} />

      {/* OAuth callback page (loaded in popup) */}
      <Route path="/accounts/oauth-complete" element={<OAuthCallback />} />

      {/* Everything else goes through the guard */}
      <Route path="/*" element={<SetupGuard />} />
    </Routes>
  )
}
