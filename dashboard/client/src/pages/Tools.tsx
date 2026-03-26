import { useState, useCallback, useMemo, useRef, useEffect } from 'react'
import { useTools, useExecuteTool, useToolSuggestions, type ToolInfo, type ToolParameterInfo, type ToolSuggestion } from '../hooks/useApi'

// ---------------------------------------------------------------------------
// Map parameter names to suggestion keys
// ---------------------------------------------------------------------------

/** Maps tool parameter names (case-insensitive) to suggestion dictionary keys. */
const PARAM_SUGGESTION_MAP: Record<string, string> = {
  accountid: 'accountId',
  account_id: 'accountId',
  folderpath: 'folderPath',
  folder_path: 'folderPath',
  folder: 'folderPath',
  folderid: 'folderId',
  folder_id: 'folderId',
  uid: 'uid',
  messageuid: 'uid',
  message_uid: 'uid',
}

function getSuggestionKey(paramName: string): string | undefined {
  return PARAM_SUGGESTION_MAP[paramName.toLowerCase()]
}

// ---------------------------------------------------------------------------
// Combobox — dropdown suggestions + free text input
// ---------------------------------------------------------------------------

function ComboboxInput({
  value,
  onChange,
  suggestions,
  placeholder,
  inputType,
  contextFilter,
}: {
  value: string
  onChange: (val: string) => void
  suggestions: ToolSuggestion[]
  placeholder: string
  inputType: string
  /** If set, filter suggestions to only those matching this accountId */
  contextFilter?: { accountId?: string }
}) {
  const [open, setOpen] = useState(false)
  const [filter, setFilter] = useState('')
  const wrapperRef = useRef<HTMLDivElement>(null)

  // Close dropdown when clicking outside
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [])

  const filtered = useMemo(() => {
    let items = suggestions
    // Filter by accountId context if available
    if (contextFilter?.accountId) {
      items = items.filter(s => !s.accountId || s.accountId === contextFilter.accountId)
    }
    // Filter by typed text
    if (filter) {
      const lower = filter.toLowerCase()
      items = items.filter(s =>
        String(s.value).toLowerCase().includes(lower) ||
        s.label.toLowerCase().includes(lower)
      )
    }
    return items
  }, [suggestions, contextFilter, filter])

  const handleInputChange = (val: string) => {
    onChange(val)
    setFilter(val)
    if (!open) setOpen(true)
  }

  const handleSelect = (s: ToolSuggestion) => {
    onChange(String(s.value))
    setFilter('')
    setOpen(false)
  }

  const selectedLabel = suggestions.find(s => String(s.value) === value)?.label

  return (
    <div ref={wrapperRef} className="relative">
      <div className="flex">
        <input
          type={inputType}
          value={value}
          onChange={(e) => handleInputChange(e.target.value)}
          onFocus={() => setOpen(true)}
          placeholder={placeholder}
          className="flex-1 border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 pr-8"
        />
        <button
          type="button"
          onClick={() => setOpen(!open)}
          className="absolute right-0 top-0 h-full px-2 text-gray-400 hover:text-gray-600"
          tabIndex={-1}
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
          </svg>
        </button>
      </div>

      {/* Selected item hint */}
      {selectedLabel && selectedLabel !== value && (
        <div className="mt-0.5 text-xs text-blue-600 truncate">{selectedLabel}</div>
      )}

      {/* Dropdown */}
      {open && filtered.length > 0 && (
        <div className="absolute z-50 mt-1 w-full bg-white border border-gray-200 rounded-lg shadow-lg max-h-48 overflow-auto">
          {filtered.map((s, i) => (
            <button
              key={`${s.value}-${i}`}
              type="button"
              onClick={() => handleSelect(s)}
              className={`w-full text-left px-3 py-2 text-sm hover:bg-blue-50 transition-colors ${
                String(s.value) === value ? 'bg-blue-50 text-blue-700' : 'text-gray-700'
              }`}
            >
              <span className="font-mono text-xs">{s.value}</span>
              <span className="ml-2 text-gray-400 text-xs">{s.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Parameter form for a single tool
// ---------------------------------------------------------------------------

function ParameterInput({
  param,
  value,
  onChange,
  suggestions,
  contextFilter,
}: {
  param: ToolParameterInfo
  value: string
  onChange: (val: string) => void
  suggestions?: ToolSuggestion[]
  contextFilter?: { accountId?: string }
}) {
  const inputType = param.type === 'integer' || param.type === 'number' ? 'number' : 'text'
  const placeholder = param.defaultValue != null ? `Default: ${param.defaultValue}` : param.required ? 'Required' : 'Optional'

  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">
        {param.name}
        {param.required && <span className="text-red-500 ml-0.5">*</span>}
        <span className="ml-2 text-xs font-normal text-gray-400">({param.type})</span>
      </label>
      {param.type === 'boolean' ? (
        <select
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
        >
          <option value="">
            {param.defaultValue != null ? `Default: ${param.defaultValue}` : '-- select --'}
          </option>
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      ) : suggestions && suggestions.length > 0 ? (
        <ComboboxInput
          value={value}
          onChange={onChange}
          suggestions={suggestions}
          placeholder={placeholder}
          inputType={inputType}
          contextFilter={contextFilter}
        />
      ) : (
        <input
          type={inputType}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
      )}
      {param.description && (
        <p className="mt-1 text-xs text-gray-500">{param.description}</p>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Tool detail / execution panel
// ---------------------------------------------------------------------------

function ToolPanel({ tool, allSuggestions }: { tool: ToolInfo; allSuggestions?: Record<string, ToolSuggestion[]> }) {
  const [paramValues, setParamValues] = useState<Record<string, string>>({})
  const [result, setResult] = useState<string | null>(null)
  const executeTool = useExecuteTool()

  const handleParamChange = useCallback((name: string, value: string) => {
    setParamValues((prev) => ({ ...prev, [name]: value }))
  }, [])

  // Derive accountId from current param values for context filtering (e.g. filter folders by selected account)
  const currentAccountId = useMemo(() => {
    for (const param of tool.parameters) {
      const key = getSuggestionKey(param.name)
      if (key === 'accountId' && paramValues[param.name]) {
        return paramValues[param.name]
      }
    }
    return undefined
  }, [tool.parameters, paramValues])

  const handleExecute = async () => {
    setResult(null)

    // Build the arguments object, converting types as needed
    const args: Record<string, unknown> = {}
    for (const param of tool.parameters) {
      const raw = paramValues[param.name]
      if (raw === undefined || raw === '') {
        // Skip optional parameters with no value (will use server default)
        if (param.required) {
          setResult(JSON.stringify({ error: `Required parameter '${param.name}' is missing.` }, null, 2))
          return
        }
        continue
      }

      if (param.type === 'integer') {
        const parsed = parseInt(raw, 10)
        if (isNaN(parsed)) {
          setResult(JSON.stringify({ error: `Parameter '${param.name}' must be a valid integer.` }, null, 2))
          return
        }
        args[param.name] = parsed
      } else if (param.type === 'number') {
        const parsed = parseFloat(raw)
        if (isNaN(parsed)) {
          setResult(JSON.stringify({ error: `Parameter '${param.name}' must be a valid number.` }, null, 2))
          return
        }
        args[param.name] = parsed
      } else if (param.type === 'boolean') {
        args[param.name] = raw === 'true'
      } else {
        args[param.name] = raw
      }
    }

    try {
      const res = await executeTool.mutateAsync({ name: tool.name, args })
      setResult(JSON.stringify(res, null, 2))
    } catch (err) {
      setResult(JSON.stringify({ error: (err as Error).message }, null, 2))
    }
  }

  return (
    <div className="flex flex-col h-full overflow-y-auto">
      {/* Tool header */}
      <div className="px-6 py-4 border-b border-gray-200 flex-shrink-0">
        <h3 className="text-lg font-semibold text-gray-900 font-mono">{tool.name}</h3>
        <p className="text-sm text-gray-500 mt-1">{tool.description}</p>
        <p className="text-xs text-gray-400 mt-1">
          Class: {tool.className} / Method: {tool.methodName}
        </p>
      </div>

      {/* Parameters form */}
      <div className="px-6 py-4 border-b border-gray-200 flex-shrink-0 space-y-4">
        {tool.parameters.length === 0 ? (
          <p className="text-sm text-gray-400">This tool has no parameters.</p>
        ) : (
          tool.parameters.map((param) => {
            const suggestionKey = getSuggestionKey(param.name)
            const suggestions = suggestionKey ? allSuggestions?.[suggestionKey] : undefined
            // For non-accountId params, pass accountId as context filter
            const contextFilter = suggestionKey !== 'accountId' && currentAccountId
              ? { accountId: currentAccountId }
              : undefined

            return (
              <ParameterInput
                key={param.name}
                param={param}
                value={paramValues[param.name] ?? ''}
                onChange={(val) => handleParamChange(param.name, val)}
                suggestions={suggestions}
                contextFilter={contextFilter}
              />
            )
          })
        )}

        <button
          onClick={handleExecute}
          disabled={executeTool.isPending}
          className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm font-medium hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {executeTool.isPending ? 'Running...' : 'Execute'}
        </button>
      </div>

      {/* Result */}
      <div className="flex-1 overflow-auto p-6">
        {result !== null ? (
          <div>
            <div className="flex items-center justify-between mb-2">
              <h4 className="text-sm font-medium text-gray-700">Response</h4>
              <button
                onClick={() => setResult(null)}
                className="text-xs text-gray-400 hover:text-gray-600"
              >
                Clear
              </button>
            </div>
            <pre className="text-xs font-mono bg-gray-50 border border-gray-200 rounded-lg p-4 whitespace-pre-wrap break-words">
              {result}
            </pre>
          </div>
        ) : (
          <div className="text-center py-12 text-gray-400 text-sm">
            Fill in the parameters and click Execute to run this tool.
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Tool category grouping
// ---------------------------------------------------------------------------

function getToolCategory(name: string): string {
  const lower = name.toLowerCase()
  if (lower.includes('account')) return 'Accounts'
  if (lower.includes('folder') && !lower.includes('analyze')) return 'Folders'
  if (lower.includes('message') || lower === 'get_thread' || lower === 'list_emails') return 'Messages'
  if (lower.includes('search')) return 'Search'
  if (lower === 'send_email' || lower === 'reply_to' || lower === 'forward') return 'Compose'
  if (['delete_messages', 'move_messages', 'mark_read', 'mark_unread', 'flag_messages', 'label_messages'].includes(lower)) return 'Organize'
  if (lower.includes('sync')) return 'Sync'
  if (['confirm_send', 'cancel_operation', 'list_pending'].includes(lower)) return 'Queue'
  if (lower.includes('analy') || lower.includes('budget') || lower === 'category_breakdown') return 'Analysis'
  if (lower.includes('report') || lower.includes('sender')) return 'Reports'
  return 'Other'
}


// ---------------------------------------------------------------------------
// Tool list sidebar
// ---------------------------------------------------------------------------

function ToolListItem({
  tool,
  isSelected,
  onClick,
}: {
  tool: ToolInfo
  isSelected: boolean
  onClick: () => void
}) {
  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-3 py-2.5 rounded-lg text-sm transition-colors ${
        isSelected
          ? 'bg-blue-50 text-blue-700 font-medium'
          : 'text-gray-700 hover:bg-gray-100'
      }`}
    >
      <div className="font-mono text-xs truncate">{tool.name}</div>
      <div className="text-xs text-gray-400 truncate mt-0.5">
        {tool.parameters.length} param{tool.parameters.length !== 1 ? 's' : ''}
      </div>
    </button>
  )
}

// ---------------------------------------------------------------------------
// Main Tools page
// ---------------------------------------------------------------------------

export default function Tools() {
  const { data: tools, isLoading, error } = useTools()
  const { data: suggestions } = useToolSuggestions()
  const [selectedToolName, setSelectedToolName] = useState<string | undefined>(undefined)
  const [filterText, setFilterText] = useState('')
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(new Set())

  const toggleGroup = useCallback((category: string) => {
    setCollapsedGroups(prev => {
      const next = new Set(prev)
      if (next.has(category)) next.delete(category)
      else next.add(category)
      return next
    })
  }, [])

  const filteredTools = tools?.filter((t) =>
    filterText
      ? t.name.toLowerCase().includes(filterText.toLowerCase()) ||
        t.description.toLowerCase().includes(filterText.toLowerCase())
      : true
  )

  const grouped = useMemo(() => {
    if (!filteredTools) return []
    const groups: Record<string, ToolInfo[]> = {}
    for (const tool of filteredTools) {
      const cat = getToolCategory(tool.name)
      ;(groups[cat] ??= []).push(tool)
    }
    // Sort groups alphabetically, sort tools within each group alphabetically
    return Object.keys(groups)
      .sort()
      .map(cat => ({
        category: cat,
        tools: groups[cat].sort((a, b) => a.name.localeCompare(b.name))
      }))
  }, [filteredTools])

  const selectedTool = tools?.find((t) => t.name === selectedToolName)

  if (isLoading) {
    return <div className="text-center py-8 text-gray-400">Loading tools...</div>
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4">
        <p className="text-sm text-red-700">{error.message}</p>
      </div>
    )
  }

  if (!tools || tools.length === 0) {
    return (
      <div className="text-center py-12 bg-white rounded-lg shadow">
        <p className="text-gray-500">No MCP tools found. Make sure the server is running with tools registered.</p>
      </div>
    )
  }

  return (
    <div className="flex flex-col h-[calc(100vh-3rem)]">
      <div className="flex items-center justify-between mb-4 flex-shrink-0">
        <h2 className="text-2xl font-semibold text-gray-900">MCP Tools</h2>
        <span className="text-sm text-gray-500">{tools.length} tools available</span>
      </div>

      <div className="flex flex-1 bg-white rounded-lg shadow overflow-hidden min-h-0">
        {/* Tool list sidebar */}
        <div className="w-64 border-r border-gray-200 flex flex-col flex-shrink-0">
          <div className="p-3 border-b border-gray-200">
            <input
              type="text"
              value={filterText}
              onChange={(e) => setFilterText(e.target.value)}
              placeholder="Filter tools..."
              className="w-full border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="flex-1 overflow-y-auto p-2 space-y-1">
            {grouped.map(({ category, tools: groupTools }) => (
              <div key={category}>
                <button
                  onClick={() => toggleGroup(category)}
                  className="w-full px-3 py-1.5 text-xs font-semibold text-gray-400 uppercase tracking-wider flex items-center justify-between hover:text-gray-600"
                >
                  <span>{category}</span>
                  <span className="text-[10px]">{collapsedGroups.has(category) ? '\u25B6' : '\u25BC'}</span>
                </button>
                {!collapsedGroups.has(category) && groupTools.map((tool) => (
                  <ToolListItem
                    key={tool.name}
                    tool={tool}
                    isSelected={tool.name === selectedToolName}
                    onClick={() => setSelectedToolName(tool.name)}
                  />
                ))}
              </div>
            ))}
            {grouped.length === 0 && (
              <div className="text-center py-4 text-gray-400 text-xs">
                No tools match &quot;{filterText}&quot;
              </div>
            )}
          </div>
        </div>

        {/* Tool detail / execution panel */}
        <div className="flex-1 overflow-hidden min-w-0">
          {selectedTool ? (
            <ToolPanel key={selectedTool.name} tool={selectedTool} allSuggestions={suggestions} />
          ) : (
            <div className="flex items-center justify-center h-full text-gray-400 text-sm">
              Select a tool from the list to view its details and execute it.
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
