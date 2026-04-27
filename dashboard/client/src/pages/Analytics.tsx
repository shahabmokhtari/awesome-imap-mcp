import { useState, useMemo, useCallback, useRef, useEffect } from 'react'
import {
  useAccounts,
  useAnalyticsSummary,
  useAnalyticsVolume,
  useAnalyticsTopSenders,
  useAnalyticsAccountBreakdown,
  useAnalyticsLabelDistribution,
  useBulkDelete,
  type TopSender,
  type AccountBreakdownEntry,
} from '../hooks/useApi'

// --- Helper: format month for display ---
function formatMonth(ym: string): string {
  const [year, month] = ym.split('-')
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
  const idx = parseInt(month, 10) - 1
  return `${months[idx] ?? month} ${year}`
}

// --- Helper: default date range (all time) ---
function defaultStartDate(): string {
  return ''
}

function defaultEndDate(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
}

// --- Color palette for bars/accounts ---
const COLORS = [
  '#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6',
  '#06b6d4', '#ec4899', '#14b8a6', '#f97316', '#6366f1',
]

const YEAR_COLORS: Record<string, string> = {
  '2026': '#3b82f6',
  '2025': '#10b981',
  '2024': '#f59e0b',
  '2023': '#ef4444',
  '2022': '#8b5cf6',
}

function getYearColor(month: string): string {
  const year = month.split('-')[0]
  return YEAR_COLORS[year] ?? '#6b7280'
}

// --- Label color mapping ---
const LABEL_COLORS: Record<string, string> = {
  '\\Seen': '#10b981',
  '\\Flagged': '#f59e0b',
  '\\Answered': '#3b82f6',
  '\\Draft': '#8b5cf6',
  '\\Deleted': '#ef4444',
  '\\Recent': '#06b6d4',
  '$Junk': '#f97316',
  '$NotJunk': '#14b8a6',
}

function getLabelColor(label: string, index: number): string {
  return LABEL_COLORS[label] ?? COLORS[index % COLORS.length]
}

function getLabelDisplayName(label: string): string {
  if (label.startsWith('\\')) return label.slice(1)
  if (label.startsWith('$')) return label.slice(1)
  return label
}

// --- Sort helpers ---
type SortKey = 'name' | 'email' | 'count' | 'last3m' | 'last6m' | 'last12m' | 'last24m' | 'firstSeen' | 'lastSeen'
type SortDir = 'asc' | 'desc'

function compareSenders(a: TopSender, b: TopSender, key: SortKey, dir: SortDir): number {
  let cmp = 0
  switch (key) {
    case 'name': cmp = (a.name || '').localeCompare(b.name || ''); break
    case 'email': cmp = (a.email || '').localeCompare(b.email || ''); break
    case 'count': cmp = a.count - b.count; break
    case 'last3m': cmp = a.last3m - b.last3m; break
    case 'last6m': cmp = a.last6m - b.last6m; break
    case 'last12m': cmp = a.last12m - b.last12m; break
    case 'last24m': cmp = a.last24m - b.last24m; break
    case 'firstSeen': cmp = (a.firstSeen || '').localeCompare(b.firstSeen || ''); break
    case 'lastSeen': cmp = (a.lastSeen || '').localeCompare(b.lastSeen || ''); break
  }
  return dir === 'asc' ? cmp : -cmp
}

// --- Delete Confirmation Dialog ---
function DeleteDialog({
  sender,
  accounts,
  onConfirm,
  onCancel,
  isPending,
}: {
  sender: TopSender
  accounts: Array<{ id: string; name: string }>
  onConfirm: (params: { senderEmail: string; accountId?: string; startDate?: string; endDate?: string; action?: string }) => void
  onCancel: () => void
  isPending: boolean
}) {
  const [accountId, setAccountId] = useState('')
  const [startDate, setStartDate] = useState('')
  const [endDate, setEndDate] = useState('')
  const [action, setAction] = useState<'delete' | 'trash' | 'archive'>('delete')

  const actionLabels = { delete: 'Delete', trash: 'Move to Trash', archive: 'Move to Archive' }
  const actionColors = { delete: 'bg-red-600 hover:bg-red-700', trash: 'bg-yellow-600 hover:bg-yellow-700', archive: 'bg-blue-600 hover:bg-blue-700' }
  const bannerColors = { delete: 'bg-red-50 border-red-200', trash: 'bg-yellow-50 border-yellow-200', archive: 'bg-blue-50 border-blue-200' }
  const textColors = { delete: 'text-red-700', trash: 'text-yellow-700', archive: 'text-blue-700' }

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
      <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-md mx-4">
        <h3 className="text-lg font-semibold text-gray-900 mb-4">Bulk Action — {sender.count.toLocaleString()} emails</h3>

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Sender Email</label>
            <input
              type="text"
              value={sender.email}
              readOnly
              className="w-full px-3 py-2 border border-gray-300 rounded-lg bg-gray-50 text-sm text-gray-700"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Action</label>
            <div className="flex rounded-lg border border-gray-300 overflow-hidden text-sm">
              {(['delete', 'trash', 'archive'] as const).map((a) => (
                <button
                  key={a}
                  onClick={() => setAction(a)}
                  className={`flex-1 px-3 py-1.5 capitalize ${action === a ? (a === 'delete' ? 'bg-red-600 text-white' : a === 'trash' ? 'bg-yellow-600 text-white' : 'bg-blue-600 text-white') : 'bg-white text-gray-600 hover:bg-gray-50'}`}
                >
                  {actionLabels[a]}
                </button>
              ))}
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Account (optional)</label>
            <select
              value={accountId}
              onChange={(e) => setAccountId(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm"
            >
              <option value="">All Accounts</option>
              {accounts.map((a) => (
                <option key={a.id} value={a.id}>{a.name}</option>
              ))}
            </select>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Start Date</label>
              <input
                type="date"
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">End Date</label>
              <input
                type="date"
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm"
              />
            </div>
          </div>

          <div className={`${bannerColors[action]} border rounded-lg p-3`}>
            <p className={`text-sm ${textColors[action]}`}>
              This will <strong>{actionLabels[action].toLowerCase()}</strong>{' '}
              <strong>all {sender.count.toLocaleString()} emails</strong> from{' '}
              <strong>{sender.email}</strong>
              {accountId ? ' in the selected account' : ' across all accounts'}
              {startDate || endDate ? ' within the specified date range' : ''}.
              {' '}Operations are chunked (50 per batch) and processed by the background worker.
            </p>
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-lg transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => onConfirm({
              senderEmail: sender.email,
              accountId: accountId || undefined,
              startDate: startDate || undefined,
              endDate: endDate || undefined,
              action,
            })}
            disabled={isPending}
            className={`px-4 py-2 text-sm text-white ${actionColors[action]} rounded-lg transition-colors disabled:opacity-50`}
          >
            {isPending ? 'Queueing...' : actionLabels[action]}
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Main Analytics Page ---
export default function Analytics() {
  // Filters
  const [startMonth, setStartMonth] = useState(defaultStartDate)
  const [endMonth, setEndMonth] = useState(defaultEndDate)
  const [accountFilter, setAccountFilter] = useState('')
  const [senderSearch, setSenderSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [sortKey, setSortKey] = useState<SortKey>('count')
  const [sortDir, setSortDir] = useState<SortDir>('desc')
  const [senderType, setSenderType] = useState<'received' | 'sent' | 'all'>('received')
  const [deleteTarget, setDeleteTarget] = useState<TopSender | null>(null)
  const [deleteResult, setDeleteResult] = useState<{ queued: number; operationIds: string[] } | null>(null)
  const volumeScrollRef = useRef<HTMLDivElement>(null)

  // Debounce search
  const [searchTimer, setSearchTimer] = useState<ReturnType<typeof setTimeout> | null>(null)
  const handleSearchChange = useCallback((value: string) => {
    setSenderSearch(value)
    if (searchTimer) clearTimeout(searchTimer)
    const timer = setTimeout(() => setDebouncedSearch(value), 300)
    setSearchTimer(timer)
  }, [searchTimer])

  // API hooks
  const { data: accounts } = useAccounts()
  const { data: summary, isLoading: summaryLoading } = useAnalyticsSummary(accountFilter || undefined)
  const { data: volume, isLoading: volumeLoading } = useAnalyticsVolume({
    startDate: startMonth ? `${startMonth}-01` : undefined,
    endDate: endMonth ? `${endMonth}-28` : undefined,
    accountId: accountFilter || undefined,
  })
  const { data: topSenders, isLoading: sendersLoading } = useAnalyticsTopSenders({
    limit: 200,
    accountId: accountFilter || undefined,
    search: debouncedSearch || undefined,
    type: senderType,
  })
  const { data: accountBreakdown, isLoading: breakdownLoading } = useAnalyticsAccountBreakdown()
  const { data: labels, isLoading: labelsLoading } = useAnalyticsLabelDistribution(accountFilter || undefined)
  const bulkDelete = useBulkDelete()

  // Auto-scroll volume chart to the right (most recent months)
  useEffect(() => {
    if (volumeScrollRef.current) {
      volumeScrollRef.current.scrollLeft = volumeScrollRef.current.scrollWidth
    }
  }, [volume])

  // Derived data
  const accountList = useMemo(() => {
    if (!accounts) return []
    return accounts.map((a: Record<string, unknown>) => ({
      id: a.id as string,
      name: a.name as string,
    }))
  }, [accounts])

  const sortedSenders = useMemo(() => {
    if (!topSenders?.senders) return []
    return [...topSenders.senders].sort((a, b) => compareSenders(a, b, sortKey, sortDir))
  }, [topSenders, sortKey, sortDir])

  const maxVolume = useMemo(() => {
    if (!volume?.months) return 1
    return Math.max(1, ...volume.months.map((m) => m.count))
  }, [volume])

  const maxAccountMessages = useMemo(() => {
    if (!accountBreakdown?.accounts) return 1
    return Math.max(1, ...accountBreakdown.accounts.map((a: AccountBreakdownEntry) => a.totalMessages))
  }, [accountBreakdown])

  const handleSort = (key: SortKey) => {
    if (sortKey === key) {
      setSortDir(sortDir === 'asc' ? 'desc' : 'asc')
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const sortIndicator = (key: SortKey) => {
    if (sortKey !== key) return ''
    return sortDir === 'asc' ? ' \u2191' : ' \u2193'
  }

  const handleBulkDelete = (params: { senderEmail: string; accountId?: string; startDate?: string; endDate?: string; action?: string }) => {
    bulkDelete.mutate(params as import('../hooks/useApi').BulkDeleteRequest, {
      onSuccess: (data) => {
        setDeleteResult(data)
        setDeleteTarget(null)
      },
    })
  }

  return (
    <div>
      <h2 className="text-2xl font-semibold text-gray-900 mb-6">Analytics</h2>

      {/* Delete confirmation dialog */}
      {deleteTarget && (
        <DeleteDialog
          sender={deleteTarget}
          accounts={accountList}
          onConfirm={handleBulkDelete}
          onCancel={() => setDeleteTarget(null)}
          isPending={bulkDelete.isPending}
        />
      )}

      {/* Delete result banner */}
      {deleteResult && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 mb-4">
          <div className="flex items-center justify-between">
            <p className="text-sm text-green-700">
              Queued {deleteResult.queued} messages for deletion
              ({deleteResult.operationIds.length} operation{deleteResult.operationIds.length !== 1 ? 's' : ''}).
              Check the Queue page for status.
            </p>
            <button
              onClick={() => setDeleteResult(null)}
              className="text-green-600 hover:text-green-800 text-sm ml-4"
            >
              Dismiss
            </button>
          </div>
        </div>
      )}

      {/* Bulk delete error */}
      {bulkDelete.error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{bulkDelete.error.message}</p>
        </div>
      )}

      {/* A. Summary Cards */}
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4 mb-8">
        <SummaryCard
          label="Total Emails"
          value={summary?.totalEmails}
          loading={summaryLoading}
          color="text-blue-600"
        />
        <SummaryCard
          label="Unique Senders"
          value={summary?.uniqueSenders}
          loading={summaryLoading}
          color="text-emerald-600"
        />
        <SummaryCard
          label="Unread"
          value={summary?.unreadCount}
          loading={summaryLoading}
          color="text-amber-600"
        />
        <SummaryCard
          label="Flagged"
          value={summary?.flaggedCount}
          loading={summaryLoading}
          color="text-red-500"
        />
        <SummaryCard
          label="With Attachments"
          value={summary?.withAttachments}
          loading={summaryLoading}
          color="text-purple-600"
        />
        <SummaryCard
          label="Months of History"
          value={summary?.monthsOfHistory}
          loading={summaryLoading}
          color="text-cyan-600"
        />
      </div>

      {/* B. Volume Bar Chart */}
      <div className="bg-white rounded-lg shadow p-5 mb-8">
        <div className="flex flex-wrap items-center gap-4 mb-4">
          <h3 className="text-lg font-semibold text-gray-900">Email Volume</h3>
          <div className="flex items-center gap-2 ml-auto">
            <label className="text-xs text-gray-500">From</label>
            <input
              type="month"
              value={startMonth}
              onChange={(e) => setStartMonth(e.target.value)}
              className="px-2 py-1 border border-gray-300 rounded text-sm"
            />
            <label className="text-xs text-gray-500">To</label>
            <input
              type="month"
              value={endMonth}
              onChange={(e) => setEndMonth(e.target.value)}
              className="px-2 py-1 border border-gray-300 rounded text-sm"
            />
            <select
              value={accountFilter}
              onChange={(e) => setAccountFilter(e.target.value)}
              className="px-2 py-1 border border-gray-300 rounded text-sm"
            >
              <option value="">All Accounts</option>
              {accountList.map((a) => (
                <option key={a.id} value={a.id}>{a.name}</option>
              ))}
            </select>
          </div>
        </div>

        {volumeLoading ? (
          <div className="text-center py-8 text-gray-400 text-sm">Loading volume data...</div>
        ) : volume?.months && volume.months.length > 0 ? (
          <div className="overflow-x-auto" ref={volumeScrollRef}>
            <div className="flex items-end gap-px min-h-[200px]" style={{ minWidth: Math.max(400, volume.months.length * 14) }}>
              {volume.months.map((m) => {
                const height = Math.max(4, (m.count / maxVolume) * 180)
                const color = getYearColor(m.month)
                const isJan = m.month.endsWith('-01')
                return (
                  <div key={m.month} className="flex flex-col items-center flex-1 min-w-[12px] group">
                    <div className="relative w-full flex justify-center">
                      <div
                        className="w-full max-w-[16px] rounded-t transition-opacity group-hover:opacity-80"
                        style={{ height: `${height}px`, backgroundColor: color }}
                        title={`${formatMonth(m.month)}: ${m.count.toLocaleString()}`}
                      />
                      <div className="absolute -top-6 left-1/2 -translate-x-1/2 bg-gray-900 text-white text-xs px-2 py-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity whitespace-nowrap pointer-events-none z-10">
                        {formatMonth(m.month)}: {m.count.toLocaleString()}
                      </div>
                    </div>
                    <span className="text-[10px] text-gray-400 mt-1 -rotate-45 origin-top-left whitespace-nowrap">
                      {isJan ? m.month.slice(0, 4) : ''}
                    </span>
                  </div>
                )
              })}
            </div>
            {/* Year legend */}
            <div className="flex gap-4 mt-4 justify-center">
              {Object.entries(YEAR_COLORS).map(([year, color]) => {
                const hasYear = volume.months.some((m) => m.month.startsWith(year))
                if (!hasYear) return null
                return (
                  <div key={year} className="flex items-center gap-1.5">
                    <div className="w-3 h-3 rounded" style={{ backgroundColor: color }} />
                    <span className="text-xs text-gray-500">{year}</span>
                  </div>
                )
              })}
            </div>
          </div>
        ) : (
          <div className="text-center py-8 text-gray-400 text-sm">No volume data available</div>
        )}
      </div>

      {/* C. Account Breakdown + D. Label Distribution side by side */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-8 mb-8">
        {/* Account Breakdown */}
        <div className="bg-white rounded-lg shadow p-5">
          <h3 className="text-lg font-semibold text-gray-900 mb-4">Account Breakdown</h3>
          {breakdownLoading ? (
            <div className="text-center py-8 text-gray-400 text-sm">Loading...</div>
          ) : accountBreakdown?.accounts && accountBreakdown.accounts.length > 0 ? (
            <div className="space-y-3">
              {accountBreakdown.accounts.map((a: AccountBreakdownEntry, idx: number) => {
                const width = Math.max(2, (a.totalMessages / maxAccountMessages) * 100)
                const color = COLORS[idx % COLORS.length]
                return (
                  <div key={a.id} className="group">
                    <div className="flex items-center justify-between mb-1">
                      <span className="text-sm text-gray-700 truncate flex-1 mr-2" title={a.email}>
                        {a.name}
                        {!a.enabled && (
                          <span className="ml-1 text-xs text-gray-400">(disabled)</span>
                        )}
                      </span>
                      <span className="text-sm font-medium text-gray-900 whitespace-nowrap">
                        {a.totalMessages.toLocaleString()}
                        <span className="text-xs text-gray-400 ml-1">
                          ({a.totalSizeMb} MB)
                        </span>
                      </span>
                    </div>
                    <div className="w-full bg-gray-100 rounded-full h-2.5">
                      <div
                        className="h-2.5 rounded-full transition-all"
                        style={{ width: `${width}%`, backgroundColor: color }}
                      />
                    </div>
                  </div>
                )
              })}
            </div>
          ) : (
            <div className="text-center py-8 text-gray-400 text-sm">No account data available</div>
          )}
        </div>

        {/* Label Distribution */}
        <div className="bg-white rounded-lg shadow p-5">
          <h3 className="text-lg font-semibold text-gray-900 mb-4">Label Distribution</h3>
          {labelsLoading ? (
            <div className="text-center py-8 text-gray-400 text-sm">Loading...</div>
          ) : labels?.labels && labels.labels.length > 0 ? (
            <div className="flex flex-wrap gap-2">
              {labels.labels.map((l, idx) => {
                const color = getLabelColor(l.name, idx)
                return (
                  <div
                    key={l.name}
                    className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full bg-gray-50 border border-gray-200"
                    title={`${l.name}: ${l.count.toLocaleString()} (${l.percentage}%)`}
                  >
                    <span
                      className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                      style={{ backgroundColor: color }}
                    />
                    <span className="text-sm text-gray-700">{getLabelDisplayName(l.name)}</span>
                    <span className="text-xs text-gray-400">
                      {l.count.toLocaleString()} ({l.percentage}%)
                    </span>
                  </div>
                )
              })}
            </div>
          ) : (
            <div className="text-center py-8 text-gray-400 text-sm">No label data available</div>
          )}
        </div>
      </div>

      {/* E. Top Senders Table */}
      <div className="bg-white rounded-lg shadow p-5">
        <div className="flex flex-wrap items-center gap-4 mb-4">
          <h3 className="text-lg font-semibold text-gray-900">Top Senders</h3>
          <div className="flex rounded-lg border border-gray-300 overflow-hidden text-sm">
            {(['received', 'sent', 'all'] as const).map((t) => (
              <button
                key={t}
                onClick={() => setSenderType(t)}
                className={`px-3 py-1 capitalize ${senderType === t ? 'bg-indigo-600 text-white' : 'bg-white text-gray-600 hover:bg-gray-50'}`}
              >
                {t}
              </button>
            ))}
          </div>
          <div className="ml-auto">
            <input
              type="text"
              value={senderSearch}
              onChange={(e) => handleSearchChange(e.target.value)}
              placeholder="Search by name, email, or domain..."
              className="px-3 py-1.5 border border-gray-300 rounded-lg text-sm w-72"
            />
          </div>
        </div>

        {sendersLoading ? (
          <div className="text-center py-8 text-gray-400 text-sm">Loading senders...</div>
        ) : sortedSenders.length > 0 ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-50 border-b">
                <tr>
                  <SortableHeader label="Name" sortKey="name" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="Email" sortKey="email" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="Total" sortKey="count" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="3M" sortKey="last3m" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="6M" sortKey="last6m" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="12M" sortKey="last12m" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="24M" sortKey="last24m" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="First Seen" sortKey="firstSeen" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <SortableHeader label="Last Seen" sortKey="lastSeen" currentKey={sortKey} dir={sortDir} onSort={handleSort} indicator={sortIndicator} />
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {sortedSenders.map((s) => (
                  <tr key={s.email} className="hover:bg-blue-50/50 transition-colors">
                    <td className="px-4 py-2.5 text-gray-900 max-w-[200px] truncate" title={s.name}>
                      {s.name !== s.email ? s.name : ''}
                    </td>
                    <td className="px-4 py-2.5 text-gray-600 font-mono text-xs max-w-[240px] truncate" title={s.email}>
                      {s.email}
                    </td>
                    <td className="px-4 py-2.5 text-gray-900 font-medium tabular-nums">{s.count.toLocaleString()}</td>
                    <td className="px-4 py-2.5 text-gray-600 tabular-nums">{s.last3m > 0 ? s.last3m.toLocaleString() : '-'}</td>
                    <td className="px-4 py-2.5 text-gray-600 tabular-nums">{s.last6m > 0 ? s.last6m.toLocaleString() : '-'}</td>
                    <td className="px-4 py-2.5 text-gray-600 tabular-nums">{s.last12m > 0 ? s.last12m.toLocaleString() : '-'}</td>
                    <td className="px-4 py-2.5 text-gray-600 tabular-nums">{s.last24m > 0 ? s.last24m.toLocaleString() : '-'}</td>
                    <td className="px-4 py-2.5 text-gray-500 text-xs whitespace-nowrap">
                      {s.firstSeen ? formatDate(s.firstSeen) : '-'}
                    </td>
                    <td className="px-4 py-2.5 text-gray-500 text-xs whitespace-nowrap">
                      {s.lastSeen ? formatDate(s.lastSeen) : '-'}
                    </td>
                    <td className="px-4 py-2.5">
                      <button
                        onClick={() => setDeleteTarget(s)}
                        className="text-red-600 hover:text-red-800 text-xs font-medium transition-colors"
                      >
                        Delete All
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="text-center py-8 text-gray-400 text-sm">
            {debouncedSearch ? 'No senders match your search' : 'No sender data available'}
          </div>
        )}

        {sortedSenders.length > 0 && (
          <div className="mt-3 text-xs text-gray-400 text-right">
            Showing {sortedSenders.length} sender{sortedSenders.length !== 1 ? 's' : ''}
          </div>
        )}
      </div>
    </div>
  )
}

// --- Summary Card Component ---
function SummaryCard({ label, value, loading, color }: {
  label: string
  value: number | undefined
  loading: boolean
  color: string
}) {
  return (
    <div className="bg-white rounded-lg shadow p-4">
      <h3 className="text-xs font-medium text-gray-500 uppercase tracking-wide">{label}</h3>
      <p className={`mt-1 text-2xl font-bold ${color}`}>
        {loading ? '...' : value != null ? value.toLocaleString() : '0'}
      </p>
    </div>
  )
}

// --- Sortable Table Header ---
function SortableHeader({ label, sortKey, onSort, indicator }: {
  label: string
  sortKey: SortKey
  currentKey?: SortKey
  dir?: SortDir
  onSort: (key: SortKey) => void
  indicator: (key: SortKey) => string
}) {
  return (
    <th
      className="text-left px-4 py-3 font-medium text-gray-500 cursor-pointer hover:text-gray-700 select-none whitespace-nowrap"
      onClick={() => onSort(sortKey)}
    >
      {label}{indicator(sortKey)}
    </th>
  )
}

// --- Date formatter ---
function formatDate(dateStr: string): string {
  try {
    const d = new Date(dateStr)
    if (isNaN(d.getTime())) return dateStr
    return d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' })
  } catch {
    return dateStr
  }
}
