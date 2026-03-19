import { useAccounts } from '../hooks/useApi'

export default function Accounts() {
  const { data: accounts, isLoading, error } = useAccounts()

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Accounts</h2>
        <button className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors">
          Add Account
        </button>
      </div>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading accounts...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {accounts && accounts.length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500 mb-4">No accounts configured yet.</p>
          <button className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700">
            Set up your first account
          </button>
        </div>
      )}

      {accounts && accounts.length > 0 && (
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
                <tr key={account.id as string} className="hover:bg-gray-50">
                  <td className="px-4 py-3 font-medium text-gray-900">
                    {account.name as string}
                  </td>
                  <td className="px-4 py-3 text-gray-600">
                    {account.provider as string}
                  </td>
                  <td className="px-4 py-3 text-gray-600">
                    {account.imapHost as string}:{account.imapPort as number}
                  </td>
                  <td className="px-4 py-3 text-gray-600">
                    {account.username as string}
                  </td>
                  <td className="px-4 py-3">
                    <button className="text-blue-600 hover:text-blue-800 text-sm mr-3">
                      Test
                    </button>
                    <button className="text-red-600 hover:text-red-800 text-sm">
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
