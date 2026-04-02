import { useState } from 'react'
import {
  useAccounts,
  useDeleteAccount,
  useTestAccount,
  useFetchRecent,
  useToggleAccountEnabled,
  useUpdateAccount,
  type RecentEmail,
  type Account,
} from '../hooks/useApi'
import AddAccountForm from '../components/AddAccountForm'

// ---------------------------------------------------------------------------
// Edit Account Modal
// ---------------------------------------------------------------------------

function EditAccountModal({ account, onClose }: { account: Account; onClose: () => void }) {
  // Parse existing configJson
  const existingConfig = account.configJson ? JSON.parse(account.configJson) : {}
  const syncConfig = existingConfig.Sync || {}

  // Basic settings
  const [name, setName] = useState(account.name)
  const [username, setUsername] = useState(account.username)

  // Server settings
  const [imapHost, setImapHost] = useState(account.imapHost)
  const [imapPort, setImapPort] = useState(account.imapPort)
  const [smtpHost, setSmtpHost] = useState(account.smtpHost ?? '')
  const [smtpPort, setSmtpPort] = useState(account.smtpPort)
  const [smtpUseSsl, setSmtpUseSsl] = useState(account.smtpUseSsl)

  // Sync settings
  const [maxConnections, setMaxConnections] = useState(syncConfig.max_connections ?? 5)
  const [pollInterval, setPollInterval] = useState(syncConfig.poll_interval ?? 300)
  const [maxMessagesPerSync, setMaxMessagesPerSync] = useState(syncConfig.max_messages_per_sync ?? 500)
  const [idleFolders, setIdleFolders] = useState((syncConfig.idle_folders || []).join(', '))

  // Queue settings
  const [confirmMode, setConfirmMode] = useState(existingConfig.confirm_mode ?? 'implicit')
  const [undoWindow, setUndoWindow] = useState(existingConfig.undo_window_seconds ?? 10)

  const updateAccount = useUpdateAccount()
  const [error, setError] = useState<string | null>(null)

  const handleSave = () => {
    setError(null)
    const configJson = JSON.stringify({
      Sync: {
        idle_folders: idleFolders.split(',').map((s: string) => s.trim()).filter(Boolean),
        poll_interval: pollInterval,
        max_messages_per_sync: maxMessagesPerSync,
        max_connections: maxConnections,
        folders: syncConfig.folders || [],
      },
      confirm_mode: confirmMode,
      undo_window_seconds: undoWindow,
    })

    updateAccount.mutate({
      id: account.id,
      name,
      imapHost,
      imapPort,
      smtpHost,
      smtpPort,
      smtpUseSsl,
      username,
      configJson,
    }, {
      onSuccess: () => onClose(),
      onError: (err) => setError(err.message),
    })
  }

  const inputClass = 'w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500'

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />

      {/* Modal */}
      <div className="relative bg-white rounded-xl shadow-2xl w-full max-w-2xl max-h-[90vh] overflow-y-auto m-4">
        <div className="sticky top-0 bg-white border-b border-gray-200 px-6 py-4 rounded-t-xl">
          <h3 className="text-lg font-bold text-gray-900">Edit Account</h3>
          <p className="text-sm text-gray-500 mt-0.5">{account.name}</p>
        </div>

        <div className="px-6 py-5 space-y-6">
          {/* Section 1: Basic Settings */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wide">Basic Settings</h4>
            <div className="space-y-3">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Name</label>
                <input type="text" value={name} onChange={e => setName(e.target.value)} className={inputClass} />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Username</label>
                <input type="text" value={username} onChange={e => setUsername(e.target.value)} className={inputClass} />
              </div>
            </div>
          </div>

          {/* Section 2: Server Settings */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wide">Server Settings</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-3 gap-3">
                <div className="col-span-2">
                  <label className="block text-xs text-gray-500 mb-1">IMAP Host</label>
                  <input type="text" value={imapHost} onChange={e => setImapHost(e.target.value)} className={inputClass} />
                </div>
                <div>
                  <label className="block text-xs text-gray-500 mb-1">IMAP Port</label>
                  <input type="number" value={imapPort} onChange={e => setImapPort(Number(e.target.value))} className={inputClass} />
                </div>
              </div>
              <div className="grid grid-cols-3 gap-3">
                <div className="col-span-2">
                  <label className="block text-xs text-gray-500 mb-1">SMTP Host</label>
                  <input type="text" value={smtpHost} onChange={e => setSmtpHost(e.target.value)} className={inputClass} />
                </div>
                <div>
                  <label className="block text-xs text-gray-500 mb-1">SMTP Port</label>
                  <input type="number" value={smtpPort} onChange={e => setSmtpPort(Number(e.target.value))} className={inputClass} />
                </div>
              </div>
              <label className="inline-flex items-center cursor-pointer">
                <input type="checkbox" checked={smtpUseSsl} onChange={e => setSmtpUseSsl(e.target.checked)} className="sr-only peer" />
                <div className="w-9 h-5 bg-gray-300 peer-focus:outline-none peer-focus:ring-2 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:bg-blue-600 relative" />
                <span className="ml-3 text-sm text-gray-700">SMTP SSL/TLS</span>
              </label>
            </div>
          </div>

          {/* Section 3: Sync Settings */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wide">Sync Settings</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Max Connections</label>
                  <input type="number" min={1} max={20} value={maxConnections} onChange={e => setMaxConnections(Number(e.target.value))} className={inputClass} />
                  <p className="text-xs text-gray-400 mt-0.5">1-20, default 5</p>
                </div>
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Poll Interval (seconds)</label>
                  <input type="number" min={30} value={pollInterval} onChange={e => setPollInterval(Number(e.target.value))} className={inputClass} />
                  <p className="text-xs text-gray-400 mt-0.5">Default 300s (5 min)</p>
                </div>
              </div>
              <div>
                <label className="block text-xs text-gray-500 mb-1">Max Messages Per Sync</label>
                <input type="number" min={1} value={maxMessagesPerSync} onChange={e => setMaxMessagesPerSync(Number(e.target.value))} className={inputClass} />
                <p className="text-xs text-gray-400 mt-0.5">Default 500</p>
              </div>
              <div>
                <label className="block text-xs text-gray-500 mb-1">IDLE Folders</label>
                <input type="text" value={idleFolders} onChange={e => setIdleFolders(e.target.value)} placeholder="INBOX, Drafts" className={inputClass} />
                <p className="text-xs text-gray-400 mt-0.5">Comma-separated folder names for IMAP IDLE</p>
              </div>
            </div>
          </div>

          {/* Section 4: Queue Settings */}
          <div>
            <h4 className="text-sm font-semibold text-gray-700 mb-3 uppercase tracking-wide">Queue Settings</h4>
            <div className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Confirm Mode</label>
                  <select value={confirmMode} onChange={e => setConfirmMode(e.target.value)} className={inputClass}>
                    <option value="implicit">Implicit</option>
                    <option value="explicit">Explicit</option>
                  </select>
                  <p className="text-xs text-gray-400 mt-0.5">Implicit: auto-confirm after undo window</p>
                </div>
                <div>
                  <label className="block text-xs text-gray-500 mb-1">Undo Window (seconds)</label>
                  <input type="number" min={0} value={undoWindow} onChange={e => setUndoWindow(Number(e.target.value))} className={inputClass} />
                  <p className="text-xs text-gray-400 mt-0.5">Default 10s</p>
                </div>
              </div>
            </div>
          </div>

          {/* Error message */}
          {error && (
            <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
              {error}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="sticky bottom-0 bg-white border-t border-gray-200 px-6 py-4 rounded-b-xl flex items-center justify-end gap-3">
          <button
            onClick={onClose}
            className="px-5 py-2 text-gray-700 bg-gray-100 rounded-lg text-sm hover:bg-gray-200 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={handleSave}
            disabled={updateAccount.isPending}
            className="px-5 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {updateAccount.isPending ? 'Saving...' : 'Save Changes'}
          </button>
        </div>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Account Row (with inline test + delete)
// ---------------------------------------------------------------------------

function AccountRow({ account, onEdit }: { account: Record<string, unknown>; onEdit: () => void }) {
  const deleteAccount = useDeleteAccount()
  const testAccount = useTestAccount()
  const fetchRecent = useFetchRecent()
  const toggleEnabled = useToggleAccountEnabled()
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [recentEmails, setRecentEmails] = useState<RecentEmail[] | null>(null)
  const [recentError, setRecentError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const isEnabled = account.enabled !== false

  const handleTest = () => {
    setTestResult(null)
    testAccount.mutate(account.id as string, {
      onSuccess: (data) => setTestResult(data),
      onError: (err) => setTestResult({ success: false, message: err.message }),
    })
  }

  const handleFetchRecent = () => {
    setRecentEmails(null)
    setRecentError(null)
    fetchRecent.mutate(account.id as string, {
      onSuccess: (data) => {
        if (data.success) setRecentEmails(data.emails)
        else setRecentError(data.message ?? 'Failed to fetch')
      },
      onError: (err) => setRecentError(err.message),
    })
  }

  const handleToggleEnabled = () => {
    toggleEnabled.mutate({ id: account.id as string, enabled: !isEnabled })
  }

  const handleDelete = () => {
    if (!confirmDelete) {
      setConfirmDelete(true)
      return
    }
    deleteAccount.mutate(account.id as string, {
      onError: (err) => setRecentError(err.message),
    })
  }

  return (
    <>
    <tr className={`hover:bg-gray-50 ${!isEnabled ? 'opacity-60' : ''}`}>
      <td className="px-4 py-3 font-medium text-gray-900">
        {account.name as string}
        {!isEnabled && (
          <span className="ml-2 text-xs px-1.5 py-0.5 rounded bg-gray-200 text-gray-500">Disabled</span>
        )}
      </td>
      <td className="px-4 py-3 text-gray-600 capitalize">{account.provider as string}</td>
      <td className="px-4 py-3 text-gray-600">
        {account.imapHost as string}:{account.imapPort as number}
      </td>
      <td className="px-4 py-3 text-gray-600">{account.username as string}</td>
      <td className="px-4 py-3">
        <div className="flex items-center gap-2 flex-wrap">
          <button
            onClick={handleToggleEnabled}
            disabled={toggleEnabled.isPending}
            className={`text-sm disabled:opacity-50 ${
              isEnabled
                ? 'text-amber-600 hover:text-amber-800'
                : 'text-green-600 hover:text-green-800'
            }`}
          >
            {toggleEnabled.isPending
              ? (isEnabled ? 'Disabling...' : 'Enabling...')
              : (isEnabled ? 'Disable' : 'Enable')}
          </button>

          <button
            onClick={onEdit}
            className="text-gray-600 hover:text-gray-800 text-sm"
          >
            Edit
          </button>

          <button
            onClick={handleTest}
            disabled={testAccount.isPending || !isEnabled}
            className="text-blue-600 hover:text-blue-800 text-sm disabled:opacity-50"
          >
            {testAccount.isPending ? 'Testing...' : 'Test'}
          </button>

          <button
            onClick={handleFetchRecent}
            disabled={fetchRecent.isPending || !isEnabled}
            className="text-indigo-600 hover:text-indigo-800 text-sm disabled:opacity-50"
          >
            {fetchRecent.isPending ? 'Fetching...' : 'Recent'}
          </button>

          {testResult && (
            <span
              className={`text-xs px-2 py-0.5 rounded ${
                testResult.success
                  ? 'bg-green-100 text-green-700'
                  : 'bg-red-100 text-red-700'
              }`}
            >
              {testResult.success ? 'OK' : testResult.message}
            </span>
          )}

          {!confirmDelete ? (
            <button
              onClick={handleDelete}
              disabled={deleteAccount.isPending}
              className="text-red-600 hover:text-red-800 text-sm disabled:opacity-50"
            >
              Delete
            </button>
          ) : (
            <>
              <span className="text-xs text-red-600">Confirm?</span>
              <button
                onClick={handleDelete}
                disabled={deleteAccount.isPending}
                className="text-red-700 hover:text-red-900 text-sm font-semibold disabled:opacity-50"
              >
                {deleteAccount.isPending ? 'Deleting...' : 'Yes, delete'}
              </button>
              <button
                onClick={() => setConfirmDelete(false)}
                className="text-gray-500 hover:text-gray-700 text-sm"
              >
                Cancel
              </button>
            </>
          )}
        </div>
      </td>
    </tr>
    {/* Recent emails expandable row */}
    {(recentEmails || recentError) && (
      <tr>
        <td colSpan={5} className="px-4 py-3 bg-gray-50">
          <div className="flex items-center justify-between mb-2">
            <span className="text-xs font-medium text-gray-500">
              {recentEmails ? `Recent emails (${recentEmails.length})` : 'Fetch failed'}
            </span>
            <button
              onClick={() => { setRecentEmails(null); setRecentError(null) }}
              className="text-xs text-gray-400 hover:text-gray-600"
            >
              Close
            </button>
          </div>
          {recentError && (
            <div className="text-xs text-red-600 bg-red-50 rounded p-2">{recentError}</div>
          )}
          {recentEmails && recentEmails.length === 0 && (
            <div className="text-xs text-gray-500">Inbox is empty.</div>
          )}
          {recentEmails && recentEmails.length > 0 && (
            <div className="space-y-1">
              {recentEmails.map((email, i) => (
                <div key={i} className="text-xs flex gap-3 py-1 border-b border-gray-100 last:border-0">
                  <span className="text-gray-400 w-32 flex-shrink-0 truncate">{email.date}</span>
                  <span className="text-gray-500 w-40 flex-shrink-0 truncate">{email.from}</span>
                  <span className="text-gray-800 truncate">{email.subject}</span>
                </div>
              ))}
            </div>
          )}
        </td>
      </tr>
    )}
    </>
  )
}

// ---------------------------------------------------------------------------
// Main Accounts page
// ---------------------------------------------------------------------------

export default function Accounts() {
  const { data: accounts, isLoading, error } = useAccounts()
  const [showAddForm, setShowAddForm] = useState(false)
  const [editingAccount, setEditingAccount] = useState<Account | null>(null)

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Accounts</h2>
        {!showAddForm && (
          <button
            onClick={() => setShowAddForm(true)}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors"
          >
            Add Account
          </button>
        )}
      </div>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading accounts...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {/* Inline add account form */}
      {showAddForm && (
        <div className="bg-white rounded-xl shadow-lg p-6 mb-6">
          <AddAccountForm
            onComplete={() => setShowAddForm(false)}
            onCancel={() => setShowAddForm(false)}
          />
        </div>
      )}

      {/* Empty state */}
      {!showAddForm && accounts && accounts.length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500 mb-4">No accounts configured yet.</p>
          <button
            onClick={() => setShowAddForm(true)}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700"
          >
            Add Account
          </button>
        </div>
      )}

      {/* Account table */}
      {!showAddForm && accounts && accounts.length > 0 && (
        <div className="bg-white rounded-lg shadow overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b">
              <tr>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Name</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Provider</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">IMAP Host</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Username</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {accounts.map((account) => (
                <AccountRow
                  key={account.id as string}
                  account={account}
                  onEdit={() => setEditingAccount(account as unknown as Account)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Edit Account Modal */}
      {editingAccount && (
        <EditAccountModal
          account={editingAccount}
          onClose={() => setEditingAccount(null)}
        />
      )}
    </div>
  )
}
