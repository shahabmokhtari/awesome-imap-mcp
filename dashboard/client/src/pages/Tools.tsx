import { useState, useCallback } from 'react'
import { useTools, useExecuteTool, type ToolInfo, type ToolParameterInfo } from '../hooks/useApi'

// ---------------------------------------------------------------------------
// Parameter form for a single tool
// ---------------------------------------------------------------------------

function ParameterInput({
  param,
  value,
  onChange,
}: {
  param: ToolParameterInfo
  value: string
  onChange: (val: string) => void
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

function ToolPanel({ tool }: { tool: ToolInfo }) {
  const [paramValues, setParamValues] = useState<Record<string, string>>({})
  const [result, setResult] = useState<string | null>(null)
  const executeTool = useExecuteTool()

  const handleParamChange = useCallback((name: string, value: string) => {
    setParamValues((prev) => ({ ...prev, [name]: value }))
  }, [])

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
        args[param.name] = parseInt(raw, 10)
      } else if (param.type === 'number') {
        args[param.name] = parseFloat(raw)
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
    <div className="flex flex-col h-full">
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
          tool.parameters.map((param) => (
            <ParameterInput
              key={param.name}
              param={param}
              value={paramValues[param.name] ?? ''}
              onChange={(val) => handleParamChange(param.name, val)}
            />
          ))
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
            <pre className="text-xs font-mono bg-gray-50 border border-gray-200 rounded-lg p-4 whitespace-pre-wrap break-words overflow-auto max-h-[60vh]">
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
  const [selectedToolName, setSelectedToolName] = useState<string | undefined>(undefined)
  const [filterText, setFilterText] = useState('')

  const filteredTools = tools?.filter((t) =>
    filterText
      ? t.name.toLowerCase().includes(filterText.toLowerCase()) ||
        t.description.toLowerCase().includes(filterText.toLowerCase())
      : true
  )

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
          <div className="flex-1 overflow-y-auto p-2 space-y-0.5">
            {filteredTools?.map((tool) => (
              <ToolListItem
                key={tool.name}
                tool={tool}
                isSelected={tool.name === selectedToolName}
                onClick={() => setSelectedToolName(tool.name)}
              />
            ))}
            {filteredTools?.length === 0 && (
              <div className="text-center py-4 text-gray-400 text-xs">
                No tools match &quot;{filterText}&quot;
              </div>
            )}
          </div>
        </div>

        {/* Tool detail / execution panel */}
        <div className="flex-1 overflow-hidden min-w-0">
          {selectedTool ? (
            <ToolPanel key={selectedTool.name} tool={selectedTool} />
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
