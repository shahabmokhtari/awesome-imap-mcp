import { useState } from 'react'
import {
  useAccounts,
  useDeleteAccount,
  useTestAccount,
  useFetchRecent,
  type RecentEmail,
} from '../hooks/useApi'
import AddAccountForm from '../components/AddAccountForm'

// ---------------------------------------------------------------------------
// Account Row (with inline test + delete)
// ---------------------------------------------------------------------------

function AccountRow({ account }: { account: Record<string, unknown> }) {
  const deleteAccount = useDeleteAccount()
  const testAccount = useTestAccount()
  const fetchRecent = useFetchRecent()
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [recentEmails, setRecentEmails] = useState<RecentEmail[] | null>(null)
  const [recentError, setRecentError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

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
    <tr className="hover:bg-gray-50">
      <td className="px-4 py-3 font-medium text-gray-900">{account.name as string}</td>
      <td className="px-4 py-3 text-gray-600 capitalize">{account.provider as string}</td>
      <td className="px-4 py-3 text-gray-600">
        {account.imapHost as string}:{account.imapPort as number}
      </td>
      <td className="px-4 py-3 text-gray-600">{account.username as string}</td>
      <td className="px-4 py-3">
        <div className="flex items-center gap-2 flex-wrap">
          <button
            onClick={handleTest}
            disabled={testAccount.isPending}
            className="text-blue-600 hover:text-blue-800 text-sm disabled:opacity-50"
          >
            {testAccount.isPending ? 'Testing...' : 'Test'}
          </button>

          <button
            onClick={handleFetchRecent}
            disabled={fetchRecent.isPending}
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
                <AccountRow key={account.id as string} account={account} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
