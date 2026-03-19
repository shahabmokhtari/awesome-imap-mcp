import { useSyncStatus } from '../hooks/useApi'

export default function Sync() {
  const { data: syncStatus, isLoading, error } = useSyncStatus()

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Sync Status</h2>
        <button className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors">
          Trigger Sync
        </button>
      </div>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading sync status...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {syncStatus && Object.keys(syncStatus).length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No sync data available yet.</p>
        </div>
      )}

      {syncStatus &&
        Object.entries(syncStatus).map(([accountId, folders]) => (
          <div key={accountId} className="mb-6">
            <h3 className="text-lg font-medium text-gray-800 mb-3">{accountId}</h3>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 border-b">
                  <tr>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Folder</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Status</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Messages</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Unread</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Last Synced</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {Array.isArray(folders) &&
                    (folders as Array<Record<string, unknown>>).map((folder, idx) => (
                      <tr key={idx} className="hover:bg-gray-50">
                        <td className="px-4 py-3 font-medium text-gray-900">
                          {(folder.displayName as string) || (folder.folderPath as string)}
                        </td>
                        <td className="px-4 py-3">
                          <span
                            className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${
                              folder.status === 'completed'
                                ? 'bg-green-100 text-green-700'
                                : folder.status === 'failed'
                                  ? 'bg-red-100 text-red-700'
                                  : 'bg-yellow-100 text-yellow-700'
                            }`}
                          >
                            {folder.status as string}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-gray-600">
                          {folder.messageCount as number}
                        </td>
                        <td className="px-4 py-3 text-gray-600">
                          {folder.unreadCount as number}
                        </td>
                        <td className="px-4 py-3 text-gray-500 text-xs">
                          {folder.lastSyncedAt as string}
                        </td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </div>
          </div>
        ))}
    </div>
  )
}
