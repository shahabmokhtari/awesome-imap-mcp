import { useState } from 'react'
import { useSyncStatus, useTriggerSync, useTriggerSyncAll, useSyncLogs } from '../hooks/useApi'
import type { SyncLogEntry } from '../hooks/useApi'

export default function Sync() {
  const { data: syncStatus, isLoading, error } = useSyncStatus()
  const triggerSync = useTriggerSync()
  const triggerSyncAll = useTriggerSyncAll()
  const [triggerResult, setTriggerResult] = useState<{ accountId: string; message: string; success: boolean } | null>(null)
  const [syncingAccountId, setSyncingAccountId] = useState<string | null>(null)

  const handleSyncAllAccounts = () => {
    setTriggerResult(null)
    triggerSyncAll.mutate(undefined, {
      onSuccess: (data) => setTriggerResult({
        accountId: '',
        message: `Sync triggered for ${data.triggered}/${data.total} account(s)${data.errors.length > 0 ? `. Errors: ${data.errors.join('; ')}` : ''}`,
        success: data.errors.length === 0,
      }),
      onError: (err) => setTriggerResult({ accountId: '', message: err.message, success: false }),
    })
  }

  const handleTriggerAll = (accountId: string) => {
    setTriggerResult(null)
    setSyncingAccountId(accountId)
    triggerSync.mutate({ accountId }, {
      onSuccess: () => { setTriggerResult({ accountId, message: 'Sync triggered', success: true }); setSyncingAccountId(null) },
      onError: (err) => { setTriggerResult({ accountId, message: err.message, success: false }); setSyncingAccountId(null) },
    })
  }

  const handleTriggerFolder = (accountId: string, folderPath: string) => {
    setTriggerResult(null)
    setSyncingAccountId(accountId)
    triggerSync.mutate({ accountId, folderPath }, {
      onSuccess: () => { setTriggerResult({ accountId, message: `Syncing ${folderPath}...`, success: true }); setSyncingAccountId(null) },
      onError: (err) => { setTriggerResult({ accountId, message: err.message, success: false }); setSyncingAccountId(null) },
    })
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Sync Status</h2>
        <button
          onClick={handleSyncAllAccounts}
          disabled={triggerSyncAll.isPending || triggerSync.isPending}
          className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm font-medium hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {triggerSyncAll.isPending ? 'Syncing All...' : 'Sync All Accounts'}
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

      {triggerResult && (
        <div className={`rounded-lg p-3 mb-4 text-sm ${
          triggerResult.success
            ? 'bg-green-50 border border-green-200 text-green-700'
            : 'bg-red-50 border border-red-200 text-red-700'
        }`}>
          {triggerResult.message}
        </div>
      )}

      {syncStatus && Object.keys(syncStatus).length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No sync data available yet. Add an account and sync will start automatically.</p>
        </div>
      )}

      {syncStatus &&
        Object.entries(syncStatus).map(([accountLabel, data]) => {
          const { accountId, folders } = data as { accountId: string; folders: unknown }
          return (
          <div key={accountLabel} className="mb-6">
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-lg font-medium text-gray-800">{accountLabel}</h3>
              <button
                onClick={() => handleTriggerAll(accountId)}
                disabled={syncingAccountId === accountId}
                className="px-3 py-1.5 bg-blue-600 text-white rounded-lg text-xs hover:bg-blue-700 transition-colors disabled:opacity-50"
              >
                {syncingAccountId === accountId ? 'Syncing...' : 'Sync All Folders'}
              </button>
            </div>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 border-b">
                  <tr>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Folder</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Status</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Messages</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Unread</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Last Synced</th>
                    <th className="text-left px-4 py-3 font-medium text-gray-500">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {Array.isArray(folders) &&
                    (folders as Array<Record<string, unknown>>).map((folder, idx) => {
                      const folderPath = (folder.folderPath as string) || ''
                      return (
                        <tr key={idx} className="hover:bg-gray-50">
                          <td className="px-4 py-3 font-medium text-gray-900">
                            {(folder.displayName as string) || folderPath}
                          </td>
                          <td className="px-4 py-3">
                            <span
                              className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${
                                folder.status === 'idle' || folder.status === 'completed'
                                  ? 'bg-green-100 text-green-700'
                                  : folder.status === 'syncing'
                                    ? 'bg-blue-100 text-blue-700'
                                    : folder.status === 'failed' || folder.status === 'error'
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
                          <td className="px-4 py-3">
                            <button
                              onClick={() => handleTriggerFolder(accountId, folderPath)}
                              disabled={syncingAccountId === accountId}
                              className="text-blue-600 hover:text-blue-800 text-xs disabled:opacity-50"
                            >
                              Sync
                            </button>
                          </td>
                        </tr>
                      )
                    })}
                </tbody>
              </table>
            </div>
            <SyncLogPanel accountId={accountId} />
          </div>
        )})}
    </div>
  )
}

function SyncLogPanel({ accountId }: { accountId: string }) {
  const { data: logs } = useSyncLogs(accountId)
  const [expanded, setExpanded] = useState(false)

  if (!logs || logs.length === 0) return null

  return (
    <div className="mt-2">
      <button
        onClick={() => setExpanded(!expanded)}
        className="text-xs text-gray-500 hover:text-gray-700 flex items-center gap-1"
      >
        {expanded ? '\u25BC' : '\u25B6'} Recent Sync Activity ({logs.length})
      </button>
      {expanded && (
        <div className="mt-2 bg-gray-50 rounded-lg border border-gray-200 overflow-hidden">
          <table className="w-full text-xs">
            <thead className="bg-gray-100">
              <tr>
                <th className="text-left px-3 py-1.5 text-gray-500">Type</th>
                <th className="text-left px-3 py-1.5 text-gray-500">Status</th>
                <th className="text-left px-3 py-1.5 text-gray-500">Messages</th>
                <th className="text-left px-3 py-1.5 text-gray-500">Duration</th>
                <th className="text-left px-3 py-1.5 text-gray-500">Time</th>
                <th className="text-left px-3 py-1.5 text-gray-500">Error</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {logs.map((log: SyncLogEntry) => (
                <tr key={log.id} className="hover:bg-gray-50">
                  <td className="px-3 py-1.5 capitalize">{log.syncType}</td>
                  <td className="px-3 py-1.5">
                    <span className={`inline-block px-1.5 py-0.5 rounded text-xs font-medium ${
                      log.status === 'completed' ? 'bg-green-100 text-green-700' :
                      log.status === 'failed' ? 'bg-red-100 text-red-700' :
                      'bg-blue-100 text-blue-700'
                    }`}>{log.status}</span>
                  </td>
                  <td className="px-3 py-1.5">{log.messagesSynced}</td>
                  <td className="px-3 py-1.5">{log.durationMs != null ? `${log.durationMs}ms` : '...'}</td>
                  <td className="px-3 py-1.5 text-gray-400">{log.startedAt}</td>
                  <td className="px-3 py-1.5 text-red-600 truncate max-w-48" title={log.errorMessage ?? ''}>{log.errorMessage ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
