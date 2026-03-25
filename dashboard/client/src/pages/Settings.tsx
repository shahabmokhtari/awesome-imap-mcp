import { useState, useEffect, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSettings, useUpdateSettings, useAuthStatus, useChangePin, useSetupPin } from '../hooks/useApi'

/** Fields that require a server restart when changed. */
const RESTART_FIELDS = new Set([
  'port', 'transport', 'host', 'listenAddress', 'dashboardPort',
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
}: {
  section: string
  values: Record<string, unknown>
  onSave: (section: string, data: Record<string, unknown>) => void
  saving: boolean
}) {
  // Filter out hidden fields for display, but keep them in save payload
  const displayEntries = Object.entries(values).filter(([key]) => !HIDDEN_FIELDS.has(key))

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
      // Check if any field differs from the original
      const isDirty = Object.keys(next).some(k => {
        if (HIDDEN_FIELDS.has(k)) return false
        const orig = values[k]
        const cur = next[k]
        if (orig === null && cur === '') return false
        if (cur === null && orig === '') return false
        return String(orig) !== String(cur)
      })
      setDirty(isDirty)
      return next
    })
  }, [values])

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
            const dropdownOpts = DROPDOWN_OPTIONS[key]

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
        {/* Dashboard PIN card - always shown */}
        <DashboardPinCard />

        {/* Settings section cards */}
        {settings && (
          <>
            {Object.entries(settings).map(([section, values]) => {
              if (typeof values !== 'object' || values === null) {
                return (
                  <div key={section} className="bg-white rounded-lg shadow p-5">
                    <h3 className="text-lg font-medium text-gray-800 mb-4 capitalize">{section}</h3>
                    <p className="text-sm text-gray-900 font-mono">{String(values)}</p>
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
