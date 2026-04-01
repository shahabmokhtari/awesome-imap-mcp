import { useState, useMemo } from 'react'
import { useQueue, useCancelOperation, useConfirmOperation, useAccounts } from '../hooks/useApi'

const statusFilters = [
  { label: 'All', value: undefined },
  { label: 'Pending', value: 'pending' },
  { label: 'Confirmed', value: 'confirmed' },
  { label: 'Processing', value: 'processing' },
  { label: 'Completed', value: 'completed' },
  { label: 'Failed', value: 'failed' },
  { label: 'Cancelled', value: 'cancelled' },
]

export default function Queue() {
  const [statusFilter, setStatusFilter] = useState<string | undefined>(undefined)
  const { data: operations, isLoading, error } = useQueue(statusFilter)
  const { data: accounts } = useAccounts()
  const cancelOp = useCancelOperation()
  const confirmOp = useConfirmOperation()
  const [expandedId, setExpandedId] = useState<string | null>(null)

  const accountNameMap = useMemo(() => {
    const map: Record<string, string> = {}
    if (accounts) {
      for (const a of accounts) {
        map[a.id as string] = a.name as string
      }
    }
    return map
  }, [accounts])

  const handleCancel = (id: string) => {
    cancelOp.mutate(id)
  }

  const handleConfirm = (id: string) => {
    confirmOp.mutate(id)
  }

  const toggleExpand = (id: string) => {
    setExpandedId(expandedId === id ? null : id)
  }

  return (
    <div>
      <h2 className="text-2xl font-semibold text-gray-900 mb-6">Operation Queue</h2>

      {/* Status filter tabs */}
      <div className="flex gap-2 mb-4 flex-wrap">
        {statusFilters.map((filter) => (
          <button
            key={filter.label}
            onClick={() => setStatusFilter(filter.value)}
            className={`px-3 py-1.5 rounded-lg text-sm transition-colors ${
              statusFilter === filter.value
                ? 'bg-blue-600 text-white'
                : 'bg-gray-200 text-gray-700 hover:bg-gray-300'
            }`}
          >
            {filter.label}
          </button>
        ))}
      </div>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading operations...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {operations && operations.length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No operations in queue.</p>
        </div>
      )}

      {operations && operations.length > 0 && (
        <div className="bg-white rounded-lg shadow overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b">
              <tr>
                <th className="text-left px-4 py-3 font-medium text-gray-500">ID</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Account</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Operation</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Status</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Retries</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Created</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {operations.map((op) => {
                const id = op.id as string
                const isExpanded = expandedId === id
                return (
                  <tr key={id} className="hover:bg-gray-50">
                    <td className="px-4 py-3 font-mono text-xs text-gray-600">
                      <button
                        onClick={() => toggleExpand(id)}
                        className="hover:text-blue-600 underline decoration-dotted"
                        title="Click to expand details"
                      >
                        {id.slice(0, 8)}...
                      </button>
                    </td>
                    <td className="px-4 py-3 text-xs">
                      {accountNameMap[op.accountId as string] ? (
                        <div>
                          <span className="text-gray-900">{accountNameMap[op.accountId as string]}</span>
                          <span className="block font-mono text-gray-400">{op.accountId as string}</span>
                        </div>
                      ) : (
                        <span className="font-mono text-gray-600">{op.accountId as string}</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-gray-900">{op.operation as string}</td>
                    <td className="px-4 py-3">
                      <div className="flex flex-col gap-1">
                        <span
                          className={`inline-block px-2 py-0.5 rounded text-xs font-medium w-fit ${
                            op.status === 'completed'
                              ? 'bg-green-100 text-green-700'
                              : op.status === 'failed'
                                ? 'bg-red-100 text-red-700'
                                : op.status === 'cancelled'
                                  ? 'bg-gray-100 text-gray-600'
                                  : op.status === 'processing'
                                    ? 'bg-blue-100 text-blue-700'
                                    : 'bg-yellow-100 text-yellow-700'
                          }`}
                        >
                          {op.status as string}
                        </span>
                        {(op.errorMessage as string | null) && (
                          <span className="text-xs text-red-600 max-w-xs truncate" title={op.errorMessage as string}>
                            {op.errorMessage as string}
                          </span>
                        )}
                        {isExpanded && (
                          <div className="mt-2 p-2 bg-gray-50 rounded text-xs space-y-1 max-w-lg">
                            <p><span className="font-medium text-gray-500">Full ID:</span> <span className="font-mono">{id}</span></p>
                            <p><span className="font-medium text-gray-500">Account:</span> {accountNameMap[op.accountId as string] ? <><span>{accountNameMap[op.accountId as string]}</span> <span className="font-mono text-gray-400">({op.accountId as string})</span></> : <span className="font-mono">{op.accountId as string}</span>}</p>
                            <p><span className="font-medium text-gray-500">Priority:</span> P{String(op.priority)}</p>
                            {(op.errorMessage as string | null) && (
                              <p><span className="font-medium text-gray-500">Error:</span> <span className="text-red-600">{op.errorMessage as string}</span></p>
                            )}
                            <details className="mt-1">
                              <summary className="cursor-pointer font-medium text-gray-500 hover:text-gray-700">Payload</summary>
                              <pre className="mt-1 p-1 bg-white rounded border text-xs overflow-x-auto whitespace-pre-wrap break-all">
                                {(() => {
                                  try { return JSON.stringify(JSON.parse(op.payload as string), null, 2) }
                                  catch { return op.payload as string }
                                })()}
                              </pre>
                            </details>
                          </div>
                        )}
                      </div>
                    </td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {op.retryCount as number}/{op.maxRetries as number}
                    </td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {op.createdAt as string}
                    </td>
                    <td className="px-4 py-3">
                      {(op.status === 'pending' || op.status === 'confirmed') && (
                        <button
                          onClick={() => handleCancel(id)}
                          disabled={cancelOp.isPending}
                          className="text-red-600 hover:text-red-800 text-sm mr-2 disabled:opacity-50"
                        >
                          Cancel
                        </button>
                      )}
                      {op.status === 'pending' && (
                        <button
                          onClick={() => handleConfirm(id)}
                          disabled={confirmOp.isPending}
                          className="text-blue-600 hover:text-blue-800 text-sm disabled:opacity-50"
                        >
                          Confirm
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
