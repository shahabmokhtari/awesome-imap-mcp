import { useState } from 'react'
import { useLogs, useLogInstances, type LogEntry } from '../hooks/useApi'

const LOG_LEVELS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'] as const

const LOG_SCOPES = [
  { value: 'All', label: 'All Scopes' },
  { value: 'mail', label: 'Mail' },
  { value: 'accounts', label: 'Accounts' },
  { value: 'api', label: 'API' },
  { value: 'mcp', label: 'MCP' },
  { value: 'queue', label: 'Queue' },
  { value: 'system', label: 'System' },
] as const

const LEVEL_COLORS: Record<string, { bg: string; text: string; dot: string }> = {
  Trace: { bg: 'bg-gray-100', text: 'text-gray-600', dot: 'bg-gray-400' },
  Debug: { bg: 'bg-gray-100', text: 'text-gray-700', dot: 'bg-gray-500' },
  Information: { bg: 'bg-blue-50', text: 'text-blue-700', dot: 'bg-blue-500' },
  Warning: { bg: 'bg-yellow-50', text: 'text-yellow-700', dot: 'bg-yellow-500' },
  Error: { bg: 'bg-red-50', text: 'text-red-700', dot: 'bg-red-500' },
  Critical: { bg: 'bg-red-100', text: 'text-red-900', dot: 'bg-red-700' },
}

const PILL_STYLES: Record<string, string> = {
  All: 'bg-gray-900 text-white',
  Trace: 'bg-gray-200 text-gray-700',
  Debug: 'bg-gray-300 text-gray-800',
  Information: 'bg-blue-100 text-blue-800',
  Warning: 'bg-yellow-100 text-yellow-800',
  Error: 'bg-red-100 text-red-800',
  Critical: 'bg-red-200 text-red-900',
}

const SCOPE_PILL_STYLES: Record<string, string> = {
  All: 'bg-gray-900 text-white',
  mail: 'bg-indigo-100 text-indigo-800',
  accounts: 'bg-emerald-100 text-emerald-800',
  api: 'bg-sky-100 text-sky-800',
  mcp: 'bg-violet-100 text-violet-800',
  queue: 'bg-amber-100 text-amber-800',
  system: 'bg-slate-200 text-slate-800',
}

const PAGE_SIZE_OPTIONS = [50, 100, 200, 500] as const

function LogRow({ log, expanded, onToggle }: { log: LogEntry; expanded: boolean; onToggle: () => void }) {
  const colors = LEVEL_COLORS[log.level] ?? LEVEL_COLORS.Information
  const time = log.created_at.split(' ')[1] ?? log.created_at

  return (
    <div className={`border-b border-gray-100 last:border-0 ${colors.bg}`}>
      <button
        onClick={onToggle}
        className="w-full text-left px-4 py-2.5 flex items-start gap-3 hover:bg-black/5 transition-colors"
      >
        <span className={`w-2 h-2 rounded-full mt-1.5 flex-shrink-0 ${colors.dot}`} />
        <span className="text-xs text-gray-400 font-mono w-20 flex-shrink-0 mt-0.5">{time}</span>
        <span className={`text-xs font-medium w-20 flex-shrink-0 mt-0.5 ${colors.text}`}>
          {log.level}
        </span>
        <span className={`text-xs font-medium w-16 flex-shrink-0 mt-0.5 px-1.5 py-0.5 rounded ${SCOPE_PILL_STYLES[log.scope] ?? 'bg-gray-100 text-gray-600'}`}>
          {log.scope}
        </span>
        <span className="text-sm text-gray-900 flex-1 break-words line-clamp-2">{log.message}</span>
      </button>

      {expanded && (
        <div className="px-4 pb-3 pl-14 space-y-2">
          <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
            <span className="text-gray-500 font-medium">Level</span>
            <span className={`font-mono ${colors.text}`}>{log.level}</span>
            <span className="text-gray-500 font-medium">Scope</span>
            <span className="font-mono text-gray-700">{log.scope}</span>
            <span className="text-gray-500 font-medium">Category</span>
            <span className="font-mono text-gray-700">{log.category}</span>
            <span className="text-gray-500 font-medium">Time</span>
            <span className="font-mono text-gray-700">{log.created_at}</span>
            <span className="text-gray-500 font-medium">Instance</span>
            <span className="font-mono text-gray-500">{log.instance_id || '(none)'}</span>
            <span className="text-gray-500 font-medium">ID</span>
            <span className="font-mono text-gray-500">{log.id}</span>
          </div>

          <div>
            <span className="text-xs text-gray-500 font-medium">Message</span>
            <pre className="mt-1 text-xs font-mono text-gray-800 bg-white/60 rounded p-2 whitespace-pre-wrap break-words border border-gray-200">
              {log.message}
            </pre>
          </div>

          {log.exception && (
            <div>
              <span className="text-xs text-red-600 font-medium">Exception</span>
              <pre className="mt-1 text-xs font-mono text-red-800 bg-red-50 rounded p-2 whitespace-pre-wrap break-words border border-red-200 max-h-64 overflow-auto">
                {log.exception}
              </pre>
            </div>
          )}

          {log.metadata && (
            <div>
              <span className="text-xs text-gray-500 font-medium">Metadata</span>
              <pre className="mt-1 text-xs font-mono text-gray-700 bg-white/60 rounded p-2 whitespace-pre-wrap break-words border border-gray-200 max-h-64 overflow-auto">
                {log.metadata}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default function Logs() {
  const [selectedLevels, setSelectedLevels] = useState<Set<string>>(new Set())
  const [scope, setScope] = useState<string>('All')
  const [instanceId, setInstanceId] = useState<string>('')
  const [search, setSearch] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(100)
  const [expandedIds, setExpandedIds] = useState<Set<number>>(new Set())

  const { data: instances } = useLogInstances()

  const levelsParam = selectedLevels.size > 0 ? Array.from(selectedLevels).join(',') : undefined

  const { data, isLoading, error } = useLogs({
    levels: levelsParam,
    scope: scope === 'All' ? undefined : scope,
    instance_id: instanceId || undefined,
    search: search || undefined,
    page,
    page_size: pageSize,
  })

  const totalPages = data ? Math.max(1, Math.ceil(data.total_count / data.page_size)) : 1

  function toggleInSet<T>(setter: React.Dispatch<React.SetStateAction<Set<T>>>, item: T) {
    setter(prev => {
      const next = new Set(prev)
      if (next.has(item)) next.delete(item)
      else next.add(item)
      return next
    })
  }

  const resetView = () => { setPage(1); setExpandedIds(new Set()) }

  const toggleLevel = (level: string) => {
    toggleInSet(setSelectedLevels, level)
    resetView()
  }

  const clearLevels = () => {
    setSelectedLevels(new Set())
    resetView()
  }

  const toggleExpanded = (id: number) => toggleInSet(setExpandedIds, id)

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault()
    setSearch(searchInput)
    resetView()
  }

  const handleScopeChange = (value: string) => {
    setScope(value)
    resetView()
  }

  const handleInstanceChange = (value: string) => {
    setInstanceId(value)
    resetView()
  }

  const handlePageSizeChange = (value: number) => {
    setPageSize(value)
    setPage(1)
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-2xl font-semibold text-gray-900">Logs</h2>
        <div className="flex items-center gap-3">
          {/* Instance picker */}
          <select
            value={instanceId}
            onChange={e => handleInstanceChange(e.target.value)}
            className="text-sm border border-gray-300 rounded-lg px-2 py-1 focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            <option value="">All instances</option>
            {instances?.map(id => (
              <option key={id} value={id}>{id}</option>
            ))}
          </select>
          {data && (
            <span className="text-sm text-gray-500">
              {data.total_count} total
            </span>
          )}
        </div>
      </div>

      {/* Level filter pills */}
      <div className="flex items-center gap-2 mb-3 flex-wrap">
        <span className="text-xs text-gray-500 font-medium mr-1">Level:</span>
        <button
          onClick={clearLevels}
          className={`px-3 py-1 rounded-full text-xs font-medium transition-colors ${
            selectedLevels.size === 0
              ? PILL_STYLES.All
              : 'bg-gray-100 text-gray-500 hover:bg-gray-200'
          }`}
        >
          All
        </button>
        {LOG_LEVELS.map(l => (
          <button
            key={l}
            onClick={() => toggleLevel(l)}
            className={`px-3 py-1 rounded-full text-xs font-medium transition-colors ${
              selectedLevels.has(l)
                ? PILL_STYLES[l]
                : 'bg-gray-100 text-gray-500 hover:bg-gray-200'
            }`}
          >
            {l}
          </button>
        ))}
      </div>

      {/* Scope filter pills */}
      <div className="flex items-center gap-2 mb-4 flex-wrap">
        <span className="text-xs text-gray-500 font-medium mr-1">Scope:</span>
        {LOG_SCOPES.map(s => (
          <button
            key={s.value}
            onClick={() => handleScopeChange(s.value)}
            className={`px-3 py-1 rounded-full text-xs font-medium transition-colors ${
              scope === s.value
                ? SCOPE_PILL_STYLES[s.value]
                : 'bg-gray-100 text-gray-500 hover:bg-gray-200'
            }`}
          >
            {s.label}
          </button>
        ))}
      </div>

      {/* Search bar */}
      <form onSubmit={handleSearch} className="flex items-center gap-2 mb-4">
        <input
          type="text"
          value={searchInput}
          onChange={e => setSearchInput(e.target.value)}
          placeholder="Search logs..."
          className="flex-1 border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
        />
        <button
          type="submit"
          className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors"
        >
          Search
        </button>
        {search && (
          <button
            type="button"
            onClick={() => { setSearch(''); setSearchInput(''); setPage(1) }}
            className="px-3 py-2 text-gray-500 bg-gray-100 rounded-lg text-sm hover:bg-gray-200"
          >
            Clear
          </button>
        )}
      </form>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading logs...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {data && data.logs.length === 0 && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No logs found{selectedLevels.size > 0 ? ` for level "${Array.from(selectedLevels).join(', ')}"` : ''}{scope !== 'All' ? ` in scope "${scope}"` : ''}{search ? ` matching "${search}"` : ''}.</p>
        </div>
      )}

      {data && data.logs.length > 0 && (
        <div className="bg-white rounded-lg shadow overflow-hidden">
          {data.logs.map(log => (
            <LogRow
              key={log.id}
              log={log}
              expanded={expandedIds.has(log.id)}
              onToggle={() => toggleExpanded(log.id)}
            />
          ))}
        </div>
      )}

      {/* Pagination controls */}
      {data && data.total_count > 0 && (
        <div className="flex items-center justify-between mt-4">
          <div className="flex items-center gap-2">
            <span className="text-sm text-gray-500">Rows per page:</span>
            <select
              value={pageSize}
              onChange={e => handlePageSizeChange(Number(e.target.value))}
              className="text-sm border border-gray-300 rounded-lg px-2 py-1 focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              {PAGE_SIZE_OPTIONS.map(size => (
                <option key={size} value={size}>{size}</option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-3">
            <span className="text-sm text-gray-600">
              Page {page} of {totalPages}
            </span>
            <div className="flex gap-1">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                className="px-3 py-1.5 text-sm rounded-lg border border-gray-300 hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
              >
                Previous
              </button>
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                className="px-3 py-1.5 text-sm rounded-lg border border-gray-300 hover:bg-gray-50 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
              >
                Next
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
