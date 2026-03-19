import { useAccounts, useSyncStatus, useQueue } from '../hooks/useApi'

export default function Overview() {
  const accounts = useAccounts()
  const syncStatus = useSyncStatus()
  const queue = useQueue()

  return (
    <div>
      <h2 className="text-2xl font-semibold text-gray-900 mb-6">Overview</h2>

      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-8">
        {/* Accounts card */}
        <div className="bg-white rounded-lg shadow p-5">
          <h3 className="text-sm font-medium text-gray-500 uppercase tracking-wide">
            Accounts
          </h3>
          <p className="mt-2 text-3xl font-bold text-gray-900">
            {accounts.isLoading ? '...' : accounts.data?.length ?? 0}
          </p>
          <p className="mt-1 text-sm text-gray-500">configured</p>
        </div>

        {/* Sync status card */}
        <div className="bg-white rounded-lg shadow p-5">
          <h3 className="text-sm font-medium text-gray-500 uppercase tracking-wide">
            Sync Status
          </h3>
          <p className="mt-2 text-3xl font-bold text-gray-900">
            {syncStatus.isLoading
              ? '...'
              : syncStatus.data
                ? Object.keys(syncStatus.data).length
                : 0}
          </p>
          <p className="mt-1 text-sm text-gray-500">active syncs</p>
        </div>

        {/* Queue card */}
        <div className="bg-white rounded-lg shadow p-5">
          <h3 className="text-sm font-medium text-gray-500 uppercase tracking-wide">
            Queue
          </h3>
          <p className="mt-2 text-3xl font-bold text-gray-900">
            {queue.isLoading ? '...' : queue.data?.length ?? 0}
          </p>
          <p className="mt-1 text-sm text-gray-500">operations</p>
        </div>
      </div>

      {/* Error display */}
      {(accounts.error || syncStatus.error || queue.error) && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4">
          <p className="text-sm text-red-700">
            {accounts.error?.message ||
              syncStatus.error?.message ||
              queue.error?.message}
          </p>
        </div>
      )}
    </div>
  )
}
