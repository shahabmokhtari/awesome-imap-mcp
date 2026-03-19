import { useState } from 'react'
import { useQueue } from '../hooks/useApi'

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
                <th className="text-left px-4 py-3 font-medium text-gray-500">Operation</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Account</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Status</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Created</th>
                <th className="text-left px-4 py-3 font-medium text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {operations.map((op) => (
                <tr key={op.id as string} className="hover:bg-gray-50">
                  <td className="px-4 py-3 font-mono text-xs text-gray-600">
                    {(op.id as string).slice(0, 8)}...
                  </td>
                  <td className="px-4 py-3 text-gray-900">{op.operation as string}</td>
                  <td className="px-4 py-3 text-gray-600">{op.accountId as string}</td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${
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
                  </td>
                  <td className="px-4 py-3 text-gray-500 text-xs">
                    {op.createdAt as string}
                  </td>
                  <td className="px-4 py-3">
                    {(op.status === 'pending' || op.status === 'confirmed') && (
                      <button className="text-red-600 hover:text-red-800 text-sm mr-2">
                        Cancel
                      </button>
                    )}
                    {op.status === 'pending' && (
                      <button className="text-blue-600 hover:text-blue-800 text-sm">
                        Confirm
                      </button>
                    )}
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
