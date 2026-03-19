import { useSettings } from '../hooks/useApi'

export default function Settings() {
  const { data: settings, isLoading, error } = useSettings()

  return (
    <div>
      <h2 className="text-2xl font-semibold text-gray-900 mb-6">Settings</h2>

      {isLoading && (
        <div className="text-center py-8 text-gray-500">Loading settings...</div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {settings && (
        <div className="space-y-6">
          {Object.entries(settings).map(([section, values]) => (
            <div key={section} className="bg-white rounded-lg shadow p-5">
              <h3 className="text-lg font-medium text-gray-800 mb-4 capitalize">
                {section}
              </h3>
              {typeof values === 'object' && values !== null ? (
                <dl className="grid grid-cols-2 gap-x-4 gap-y-2">
                  {Object.entries(values as Record<string, unknown>).map(([key, value]) => (
                    <div key={key} className="contents">
                      <dt className="text-sm text-gray-500">{key}</dt>
                      <dd className="text-sm text-gray-900 font-mono">
                        {value === null
                          ? 'null'
                          : typeof value === 'boolean'
                            ? value
                              ? 'true'
                              : 'false'
                            : String(value)}
                      </dd>
                    </div>
                  ))}
                </dl>
              ) : (
                <p className="text-sm text-gray-900 font-mono">{String(values)}</p>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
