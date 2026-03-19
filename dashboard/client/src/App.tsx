import { Routes, Route, NavLink } from 'react-router-dom'
import Overview from './pages/Overview'
import Accounts from './pages/Accounts'
import Sync from './pages/Sync'
import Queue from './pages/Queue'
import Settings from './pages/Settings'

const navItems = [
  { to: '/', label: 'Overview' },
  { to: '/accounts', label: 'Accounts' },
  { to: '/sync', label: 'Sync' },
  { to: '/queue', label: 'Queue' },
  { to: '/settings', label: 'Settings' },
]

export default function App() {
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
        <div className="px-4 py-3 border-t border-gray-700 text-xs text-gray-500">
          ultimate-imap-mcp v0.1.0
        </div>
      </nav>

      {/* Main content */}
      <main className="flex-1 overflow-auto p-6">
        <Routes>
          <Route path="/" element={<Overview />} />
          <Route path="/accounts" element={<Accounts />} />
          <Route path="/sync" element={<Sync />} />
          <Route path="/queue" element={<Queue />} />
          <Route path="/settings" element={<Settings />} />
        </Routes>
      </main>
    </div>
  )
}
