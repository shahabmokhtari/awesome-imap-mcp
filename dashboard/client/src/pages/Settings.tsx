import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSettings, useUpdateSettings, useAuthStatus, useChangePin, useSetupPin, useLlmModels, useTestLlm, useServerInfo, useShutdownServer, useInstances, useShutdownInstance, useAccounts, useClearCache, useClearAccountCache, type InstanceHeartbeat } from '../hooks/useApi'

/** Fields that require a server restart when changed. */
const RESTART_FIELDS = new Set([
  'httpPort', 'transport', 'dashboardPort',
])

/** Fields that should be skipped in the SectionCard (handled separately). */
const HIDDEN_FIELDS = new Set(['dashboardAuth'])

/** Fields that should render as dropdown selects with fixed options. */
const DROPDOWN_OPTIONS: Record<string, string[]> = {
  transport: ['stdio', 'http', 'both'],
  logLevel: ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'],
  provider: ['anthropic', 'openai', 'acp_claude', 'acp_copilot', 'in_context'],
  otlpProtocol: ['grpc', 'http'],
}

function fieldLabel(key: string): string {
  // Turn camelCase into spaced words
  return key.replace(/([A-Z])/g, ' $1').replace(/^./, s => s.toUpperCase())
}

function SectionCard({
  section,
  values,
  onSave,
  saving,
  dropdownOverrides,
  skipFields,
  onFormChange,
}: {
  section: string
  values: Record<string, unknown>
  onSave: (section: string, data: Record<string, unknown>) => void
  saving: boolean
  /** Dynamic dropdown options that override DROPDOWN_OPTIONS for specific fields. */
  dropdownOverrides?: Record<string, string[] | undefined>
  /** Additional fields to hide from display (beyond HIDDEN_FIELDS). */
  skipFields?: Set<string>
  /** Called when any form field changes, with the updated form state. */
  onFormChange?: (form: Record<string, unknown>) => void
}) {
  const displayEntries = Object.entries(values).filter(([key]) => {
    if (HIDDEN_FIELDS.has(key)) return false
    if (skipFields?.has(key)) return false
    return true
  })

  const [form, setForm] = useState<Record<string, unknown>>({})
  const [dirty, setDirty] = useState(false)

  // Reset form when upstream data changes
  useEffect(() => {
    setForm({ ...values })
    setDirty(false)
  }, [values])

  const handleChange = useCallback((key: string, value: unknown) => {
    setForm(prev => {
      const next = { ...prev, [key]: value }
      const isDirty = Object.keys(next).some(k => {
        if (HIDDEN_FIELDS.has(k)) return false
        const orig = values[k]
        const cur = next[k]
        if (orig === null && cur === '') return false
        if (cur === null && orig === '') return false
        return String(orig) !== String(cur)
      })
      setDirty(isDirty)
      onFormChange?.(next)
      return next
    })
  }, [values, onFormChange])

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    // Exclude hidden fields from the save payload
    const payload = Object.fromEntries(
      Object.entries(form).filter(([key]) => !HIDDEN_FIELDS.has(key))
    )
    onSave(section, payload)
  }

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-4 capitalize">{section}</h3>
      <form onSubmit={handleSubmit}>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-4">
          {displayEntries.map(([key, originalValue]) => {
            const currentValue = form[key]
            const isBoolean = typeof originalValue === 'boolean'
            const isNumber = typeof originalValue === 'number'
            const dropdownOpts = (dropdownOverrides && key in dropdownOverrides)
              ? dropdownOverrides[key]
              : DROPDOWN_OPTIONS[key]

            return (
              <div key={key} className="flex flex-col">
                <label className="text-sm font-medium text-gray-600 mb-1">
                  {fieldLabel(key)}
                </label>

                {isBoolean ? (
                  <label className="relative inline-flex items-center cursor-pointer mt-1">
                    <input
                      type="checkbox"
                      checked={!!currentValue}
                      onChange={e => handleChange(key, e.target.checked)}
                      className="sr-only peer"
                    />
                    <div className="w-9 h-5 bg-gray-300 peer-focus:outline-none peer-focus:ring-2 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:bg-blue-600" />
                    <span className="ml-3 text-sm text-gray-700">
                      {currentValue ? 'Enabled' : 'Disabled'}
                    </span>
                  </label>
                ) : dropdownOpts ? (
                  <select
                    value={currentValue === null || currentValue === undefined ? '' : String(currentValue)}
                    onChange={e => handleChange(key, e.target.value)}
                    className="border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  >
                    {dropdownOpts.map(opt => (
                      <option key={opt} value={opt}>{opt}</option>
                    ))}
                  </select>
                ) : isNumber ? (
                  <input
                    type="number"
                    value={currentValue === null || currentValue === undefined ? '' : String(currentValue)}
                    onChange={e => {
                      const raw = e.target.value
                      handleChange(key, raw === '' ? null : Number(raw))
                    }}
                    className="border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  />
                ) : (
                  <input
                    type="text"
                    value={currentValue === null || currentValue === undefined ? '' : String(currentValue)}
                    onChange={e => handleChange(key, e.target.value)}
                    className="border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                  />
                )}
              </div>
            )
          })}
        </div>

        {dirty && (
          <div className="mt-5 flex items-center gap-3">
            <button
              type="submit"
              disabled={saving}
              className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {saving ? 'Saving...' : 'Save Changes'}
            </button>
            <button
              type="button"
              onClick={() => { setForm({ ...values }); setDirty(false) }}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-lg text-sm hover:bg-gray-300 transition-colors"
            >
              Reset
            </button>
          </div>
        )}
      </form>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Server Controls card
// ---------------------------------------------------------------------------

function formatUptimeFromStart(startedAt: string): string {
  const ms = Date.now() - new Date(startedAt).getTime()
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${s % 60}s`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m`
}

function truncatePath(cwd: string): string {
  const parts = cwd.replace(/\\/g, '/').split('/').filter(Boolean)
  return parts.slice(-3).join('/') || cwd
}

function InstanceRow({ instance, isCurrent, onShutdown }: {
  instance: InstanceHeartbeat
  isCurrent: boolean
  onShutdown: (id: string) => void
}) {
  const cwdDisplay = truncatePath(instance.cwd) + (isCurrent ? ' (this)' : '')

  return (
    <tr className="border-b border-gray-100 last:border-0">
      <td className="px-3 py-2 text-sm font-mono text-gray-900">{instance.processId}</td>
      <td className="px-3 py-2 text-sm font-mono text-gray-700 max-w-[180px] truncate" title={instance.cwd}>
        {cwdDisplay}
      </td>
      <td className="px-3 py-2 text-sm text-gray-700">{instance.transport}</td>
      <td className="px-3 py-2 text-sm">
        <div className="flex flex-wrap gap-1">
          {instance.isLeader && (
            <span className="px-1.5 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-800">Leader</span>
          )}
          {instance.isDashboardHost && (
            <span className="px-1.5 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">Dashboard</span>
          )}
        </div>
      </td>
      <td className="px-3 py-2 text-sm text-gray-700">{formatUptimeFromStart(instance.startedAt)}</td>
      <td className="px-3 py-2 text-sm text-gray-700">{(instance.cpuTimeMs / 1000).toFixed(1)}s</td>
      <td className="px-3 py-2 text-sm text-gray-700">{instance.memoryMb} MB</td>
      <td className="px-3 py-2">
        <button
          onClick={() => onShutdown(instance.instanceId)}
          disabled={isCurrent}
          className="px-2 py-1 text-xs bg-red-50 text-red-700 border border-red-200 rounded hover:bg-red-100 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
        >
          Shutdown
        </button>
      </td>
    </tr>
  )
}

function ServerControlsCard() {
  const { data: serverInfo } = useServerInfo()
  const { data: instancesData, isLoading: instancesLoading } = useInstances()
  const shutdownInstance = useShutdownInstance()
  const shutdownMutation = useShutdownServer()
  const [confirming, setConfirming] = useState(false)
  const [shutdownSent, setShutdownSent] = useState(false)
  const [disconnected, setDisconnected] = useState(false)
  const [instanceError, setInstanceError] = useState<string | null>(null)

  const handleShutdown = () => {
    shutdownMutation.mutate(undefined, {
      onSuccess: () => {
        setShutdownSent(true)
        setConfirming(false)
        setTimeout(() => setDisconnected(true), 3000)
      },
      onError: (err) => {
        setConfirming(false)
        setInstanceError(`Shutdown failed: ${err.message}`)
      },
    })
  }

  const handleShutdownInstance = (instanceId: string) => {
    if (!window.confirm(`Shutdown instance ${instanceId}?`)) return
    setInstanceError(null)
    shutdownInstance.mutate(instanceId, {
      onError: (err) => setInstanceError(`Failed to shutdown ${instanceId}: ${err.message}`),
    })
  }

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-4">Server Controls</h3>

      {instancesLoading && (
        <div className="text-sm text-gray-500 mb-4">Loading instances...</div>
      )}

      {instancesData && instancesData.instances.length > 0 && (
        <div className="mb-5 overflow-x-auto">
          <table className="w-full text-left border-collapse">
            <thead>
              <tr className="border-b border-gray-200">
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">PID</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">CWD</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">Transport</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">Role</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">Uptime</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">CPU</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">Mem</th>
                <th className="px-3 py-2 text-xs font-medium text-gray-500 uppercase">Actions</th>
              </tr>
            </thead>
            <tbody>
              {instancesData.instances.map(instance => (
                <InstanceRow
                  key={instance.instanceId}
                  instance={instance}
                  isCurrent={instance.instanceId === instancesData.current}
                  onShutdown={handleShutdownInstance}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {instanceError && (
        <div className="mt-2 text-xs text-red-600 bg-red-50 border border-red-200 rounded p-2">
          {instanceError}
        </div>
      )}

      {serverInfo && !instancesData && !instancesLoading && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
          <div>
            <span className="block text-xs text-gray-500">Instance ID</span>
            <span className="text-sm font-mono text-gray-900 break-all">{serverInfo.instance_id}</span>
          </div>
          <div>
            <span className="block text-xs text-gray-500">PID</span>
            <span className="text-sm font-mono text-gray-900">{serverInfo.process_id}</span>
          </div>
          <div>
            <span className="block text-xs text-gray-500">Version</span>
            <span className="text-sm text-gray-900">{serverInfo.version}</span>
          </div>
        </div>
      )}

      {disconnected && (
        <div className="bg-gray-100 border border-gray-300 rounded-lg p-3 mb-4 text-sm text-gray-700">
          Server has been shut down. Refresh to reconnect.
        </div>
      )}

      {shutdownSent && !disconnected && (
        <div className="bg-amber-50 border border-amber-300 rounded-lg p-3 mb-4 text-sm text-amber-800">
          Shutting down...
        </div>
      )}

      {!shutdownSent && !confirming && (
        <button
          onClick={() => setConfirming(true)}
          className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm hover:bg-red-700 transition-colors"
        >
          Shutdown Server
        </button>
      )}

      {!shutdownSent && confirming && (
        <div className="flex items-center gap-3">
          <span className="text-sm text-gray-700">Are you sure? The dashboard will disconnect.</span>
          <button
            onClick={handleShutdown}
            disabled={shutdownMutation.isPending}
            className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm hover:bg-red-700 transition-colors disabled:opacity-50"
          >
            {shutdownMutation.isPending ? 'Shutting down...' : 'Confirm Shutdown'}
          </button>
          <button
            onClick={() => setConfirming(false)}
            className="px-4 py-2 bg-gray-200 text-gray-700 rounded-lg text-sm hover:bg-gray-300 transition-colors"
          >
            Cancel
          </button>
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Dashboard PIN card
// ---------------------------------------------------------------------------

function DashboardPinCard() {
  const { data: authStatus, refetch: refetchAuth } = useAuthStatus()
  const changePin = useChangePin()
  const setupPin = useSetupPin()
  const hasPinSet = authStatus?.hasPinSet ?? false

  const [expanded, setExpanded] = useState(false)
  const [oldPin, setOldPin] = useState('')
  const [newPin, setNewPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)

  const resetForm = () => {
    setOldPin('')
    setNewPin('')
    setConfirmPin('')
    setError(null)
    setSuccess(null)
  }

  const handleToggle = () => {
    if (expanded) {
      resetForm()
    }
    setExpanded(!expanded)
  }

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setSuccess(null)

    if (newPin.length < 4 || newPin.length > 6) {
      setError('PIN must be 4-6 digits.')
      return
    }
    if (!/^\d+$/.test(newPin)) {
      setError('PIN must contain only digits.')
      return
    }
    if (newPin !== confirmPin) {
      setError('PINs do not match.')
      return
    }

    if (hasPinSet) {
      // Change existing PIN
      changePin.mutate({ old_pin: oldPin, new_pin: newPin }, {
        onSuccess: (data) => {
          if (data.token) {
            localStorage.setItem('dashboard_token', data.token)
          }
          setSuccess(data.message || 'PIN updated successfully.')
          resetForm()
          setExpanded(false)
          refetchAuth()
        },
        onError: (err) => setError(err.message),
      })
    } else {
      // Set initial PIN via setup endpoint
      setupPin.mutate(newPin, {
        onSuccess: (data) => {
          if (data.token) {
            localStorage.setItem('dashboard_token', data.token)
          }
          setSuccess('PIN set successfully.')
          resetForm()
          setExpanded(false)
          refetchAuth()
        },
        onError: (err) => setError(err.message),
      })
    }
  }

  const isPending = changePin.isPending || setupPin.isPending

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-4">Dashboard PIN</h3>

      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <span className={`inline-block w-2 h-2 rounded-full ${hasPinSet ? 'bg-green-500' : 'bg-gray-300'}`} />
          <span className="text-sm text-gray-700">
            {hasPinSet ? 'PIN is set' : 'No PIN set'}
          </span>
        </div>
        {!expanded && (
          <button
            onClick={handleToggle}
            className="px-4 py-2 text-sm text-blue-600 bg-blue-50 border border-blue-200 rounded-lg hover:bg-blue-100 transition-colors"
          >
            {hasPinSet ? 'Change PIN' : 'Set PIN'}
          </button>
        )}
      </div>

      {success && !expanded && (
        <div className="mt-3 bg-green-50 border border-green-200 rounded-lg p-3 text-sm text-green-700">
          {success}
        </div>
      )}

      {expanded && (
        <form onSubmit={handleSubmit} className="mt-4 space-y-4 max-w-sm">
          {hasPinSet && (
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Current PIN</label>
              <input
                type="password"
                inputMode="numeric"
                maxLength={6}
                value={oldPin}
                onChange={e => setOldPin(e.target.value)}
                placeholder="Enter current PIN"
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>
          )}

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">New PIN (4-6 digits)</label>
            <input
              type="password"
              inputMode="numeric"
              maxLength={6}
              value={newPin}
              onChange={e => setNewPin(e.target.value)}
              placeholder="Enter new PIN"
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Confirm New PIN</label>
            <input
              type="password"
              inputMode="numeric"
              maxLength={6}
              value={confirmPin}
              onChange={e => setConfirmPin(e.target.value)}
              placeholder="Confirm new PIN"
              className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
            />
          </div>

          {error && (
            <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
              {error}
            </div>
          )}

          <div className="flex items-center gap-3 pt-2">
            <button
              type="submit"
              disabled={isPending}
              className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {isPending ? 'Saving...' : hasPinSet ? 'Update PIN' : 'Set PIN'}
            </button>
            <button
              type="button"
              onClick={handleToggle}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-lg text-sm hover:bg-gray-300 transition-colors"
            >
              Cancel
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------
// LLM Settings card — reuses SectionCard with dynamic model dropdown
// ---------------------------------------------------------------------------

/** Providers that don't need an API key. */
const KEYLESS_PROVIDERS = new Set(['acp_claude', 'acp_copilot', 'in_context'])

function LlmSectionCard({
  values,
  onSave,
  saving,
}: {
  values: Record<string, unknown>
  onSave: (section: string, data: Record<string, unknown>) => void
  saving: boolean
}) {
  const [currentProvider, setCurrentProvider] = useState(values.provider as string | undefined)
  const { data: modelOptions } = useLlmModels(currentProvider)
  const hasModels = modelOptions && modelOptions.length > 0
  const isKeyless = KEYLESS_PROVIDERS.has(currentProvider ?? '')

  // Per-provider API key state
  const providerApiKeys = (values.providerApiKeys ?? {}) as Record<string, string>
  const [providerKeys, setProviderKeys] = useState<Record<string, string>>({ ...providerApiKeys })
  const [keysDirty, setKeysDirty] = useState(false)

  // Sync provider from upstream when settings are reloaded
  useEffect(() => {
    setCurrentProvider(values.provider as string | undefined)
  }, [values.provider])

  useEffect(() => {
    setProviderKeys({ ...(values.providerApiKeys ?? {}) as Record<string, string> })
    setKeysDirty(false)
  }, [values.providerApiKeys])

  const handleFormChange = useCallback((form: Record<string, unknown>) => {
    const provider = form.provider as string | undefined
    setCurrentProvider(prev => prev === provider ? prev : provider)
  }, [])

  // Wrap onSave to include providerApiKeys
  const handleSave = useCallback((section: string, data: Record<string, unknown>) => {
    if (keysDirty) {
      data.providerApiKeys = providerKeys
    }
    onSave(section, data)
    setKeysDirty(false)
  }, [onSave, providerKeys, keysDirty])

  const handleKeyChange = useCallback((provider: string, value: string) => {
    setProviderKeys(prev => {
      const next = { ...prev, [provider]: value }
      setKeysDirty(true)
      return next
    })
  }, [])

  return (
    <div className="space-y-4">
      <SectionCard
        section="llm"
        values={values}
        onSave={handleSave}
        saving={saving}
        dropdownOverrides={{ model: hasModels ? modelOptions : undefined }}
        skipFields={new Set([
          ...(hasModels ? [] : ['model']),
          'providerApiKeys',
          ...(isKeyless ? ['dailyTokenBudget', 'monthlyCostLimit'] : []),
        ])}
        onFormChange={handleFormChange}
      />

      <div className="bg-white rounded-lg shadow p-5">
        <h3 className="text-lg font-medium text-gray-800 mb-4">API Keys</h3>

        {isKeyless ? (
          <div className="bg-green-50 border border-green-200 rounded-lg p-3 text-sm text-green-700">
            Provider <span className="font-medium">{currentProvider}</span> does not require an API key.
          </div>
        ) : (
          <div className="space-y-4">
            <p className="text-sm text-gray-600">
              Set a provider-specific API key below, or use the global key configured in config.json.
            </p>
            {['openai', 'anthropic'].map(p => (
              <div key={p} className="flex flex-col">
                <label className="text-sm font-medium text-gray-600 mb-1 capitalize">
                  {p} API Key
                </label>
                <input
                  type="password"
                  value={providerKeys[p] ?? ''}
                  onChange={e => handleKeyChange(p, e.target.value)}
                  placeholder={providerKeys[p] === '***' ? '(unchanged)' : 'Enter API key'}
                  className="border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 max-w-md"
                />
              </div>
            ))}
            {keysDirty && (
              <p className="text-xs text-amber-600">
                Provider API key changes will be saved when you click &quot;Save Changes&quot; in the LLM section above.
              </p>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// LLM Test Panel
// ---------------------------------------------------------------------------

function LlmTestPanel({ settings }: { settings: Record<string, unknown> }) {
  const llm = settings.llm as Record<string, unknown> | undefined
  const isEnabled = llm?.enabled === true

  const [prompt, setPrompt] = useState('Hello, respond with a short greeting')
  const testLlm = useTestLlm()

  if (!isEnabled) return null

  const handleTest = () => {
    testLlm.mutate({
      prompt,
      provider: llm?.provider as string | undefined,
      model: llm?.model as string | undefined,
    })
  }

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-4">LLM Test</h3>

      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium text-gray-600 mb-1">Prompt</label>
          <textarea
            value={prompt}
            onChange={e => setPrompt(e.target.value)}
            rows={3}
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
          />
        </div>

        <button
          onClick={handleTest}
          disabled={testLlm.isPending || !prompt.trim()}
          className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {testLlm.isPending ? 'Testing...' : 'Test LLM'}
        </button>

        {testLlm.data && (
          <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-2">
            <p className="text-sm text-gray-900 whitespace-pre-wrap">{testLlm.data.response}</p>
            <div className="flex gap-4 text-xs text-gray-500">
              <span>Model: {testLlm.data.model}</span>
              <span>Duration: {testLlm.data.duration_ms}ms</span>
            </div>
          </div>
        )}

        {testLlm.error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4">
            <p className="text-sm text-red-700">{testLlm.error.message}</p>
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Cache Management card
// ---------------------------------------------------------------------------

function CacheManagementCard() {
  const { data: accounts } = useAccounts()
  const clearCache = useClearCache()
  const clearAccountCache = useClearAccountCache()
  const [resultMsg, setResultMsg] = useState<string | null>(null)

  const handleClearAll = () => {
    if (!window.confirm('Clear ALL cached messages? This cannot be undone.')) return
    clearCache.mutate(undefined, {
      onSuccess: (data) => {
        setResultMsg(`Cleared ${data.deleted} cached messages.`)
        setTimeout(() => setResultMsg(null), 5000)
      },
      onError: (err) => {
        setResultMsg(`Error: ${err.message}`)
        setTimeout(() => setResultMsg(null), 5000)
      },
    })
  }

  const handleClearAccount = (accountId: string) => {
    if (!window.confirm('Clear cached messages for this account?')) return
    clearAccountCache.mutate(accountId, {
      onSuccess: (data) => {
        setResultMsg(`Cleared ${data.deleted} cached messages for account.`)
        setTimeout(() => setResultMsg(null), 5000)
      },
      onError: (err) => {
        setResultMsg(`Error: ${err.message}`)
        setTimeout(() => setResultMsg(null), 5000)
      },
    })
  }

  return (
    <div className="bg-white rounded-lg shadow p-5">
      <h3 className="text-lg font-medium text-gray-800 mb-4">Cache Management</h3>

      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-gray-700">Clear all cached messages across every account.</p>
          </div>
          <button
            onClick={handleClearAll}
            disabled={clearCache.isPending}
            className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {clearCache.isPending ? 'Clearing...' : 'Clear All Cache'}
          </button>
        </div>

        {accounts && accounts.length > 0 && (
          <div className="border-t border-gray-200 pt-4">
            <p className="text-sm font-medium text-gray-600 mb-3">Per-Account Cache</p>
            <div className="space-y-2">
              {accounts.map((a) => (
                <div key={a.id as string} className="flex items-center justify-between py-1">
                  <span className="text-sm text-gray-700">{a.name as string}</span>
                  <button
                    onClick={() => handleClearAccount(a.id as string)}
                    disabled={clearAccountCache.isPending}
                    className="px-3 py-1 text-xs text-red-600 bg-red-50 border border-red-200 rounded hover:bg-red-100 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {clearAccountCache.isPending ? 'Clearing...' : 'Clear'}
                  </button>
                </div>
              ))}
            </div>
          </div>
        )}

        {resultMsg && (
          <div className={`rounded-lg p-3 text-sm ${
            resultMsg.startsWith('Error')
              ? 'bg-red-50 border border-red-200 text-red-700'
              : 'bg-green-50 border border-green-200 text-green-700'
          }`}>
            {resultMsg}
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Main Settings page
// ---------------------------------------------------------------------------

export default function Settings() {
  const { data: settings, isLoading, error } = useSettings()
  const updateSettings = useUpdateSettings()
  const navigate = useNavigate()

  const [savedSection, setSavedSection] = useState<string | null>(null)
  const [needsRestart, setNeedsRestart] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const handleRunWizard = () => {
    localStorage.removeItem('setup_completed')
    navigate('/setup')
  }

  const handleSave = (section: string, data: Record<string, unknown>) => {
    setSaveError(null)
    setSavedSection(null)
    setNeedsRestart(false)

    // Check if any restart-required fields changed
    const original = (settings as Record<string, Record<string, unknown>>)?.[section] ?? {}
    const changedKeys = Object.keys(data).filter(k => String(data[k]) !== String(original[k]))
    const willNeedRestart = changedKeys.some(k => RESTART_FIELDS.has(k))

    updateSettings.mutate(
      { [section]: data },
      {
        onSuccess: () => {
          setSavedSection(section)
          if (willNeedRestart) setNeedsRestart(true)
          setTimeout(() => setSavedSection(null), 4000)
        },
        onError: (err) => {
          setSaveError(err.message)
        },
      },
    )
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Settings</h2>
        <button
          onClick={handleRunWizard}
          className="px-4 py-2 text-sm text-gray-700 bg-gray-100 rounded-lg hover:bg-gray-200 transition-colors"
        >
          Run Setup Wizard
        </button>
      </div>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading settings...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {saveError && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">Failed to save: {saveError}</p>
        </div>
      )}

      {savedSection && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-green-700">
            <span className="capitalize">{savedSection}</span> settings saved successfully.
          </p>
          {needsRestart && (
            <p className="text-sm text-amber-700 mt-1">
              Some changes (port, transport, host) require a server restart to take effect.
            </p>
          )}
        </div>
      )}

      <div className="space-y-6">
        {/* Server controls card - always shown */}
        <ServerControlsCard />

        {/* Dashboard PIN card - always shown */}
        <DashboardPinCard />

        {/* Cache management card */}
        <CacheManagementCard />

        {/* Settings section cards */}
        {settings && (
          <>
            {Object.entries(settings as Record<string, unknown>).map(([section, values]) => {
              if (typeof values !== 'object' || values === null) {
                return (
                  <div key={section} className="bg-white rounded-lg shadow p-5">
                    <h3 className="text-lg font-medium text-gray-800 mb-4 capitalize">{section}</h3>
                    <p className="text-sm text-gray-900 font-mono">{String(values)}</p>
                  </div>
                )
              }
              if (section === 'llm') {
                return (
                  <div key={section} className="space-y-6">
                    <LlmSectionCard
                      values={values as Record<string, unknown>}
                      onSave={handleSave}
                      saving={updateSettings.isPending}
                    />
                    <LlmTestPanel settings={settings as Record<string, unknown>} />
                  </div>
                )
              }
              return (
                <SectionCard
                  key={section}
                  section={section}
                  values={values as Record<string, unknown>}
                  onSave={handleSave}
                  saving={updateSettings.isPending}
                />
              )
            })}
          </>
        )}
      </div>
    </div>
  )
}
