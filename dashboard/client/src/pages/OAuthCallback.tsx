import { useEffect, useState } from 'react'

type CallbackResult =
  | { status: 'done'; errorMessage: null; tempId: string; email: string | null; name: string | null }
  | { status: 'error'; errorMessage: string }

function readCallbackParams(): CallbackResult {
  const params = new URLSearchParams(window.location.search)
  const tempId = params.get('temp_id')
  const error = params.get('error')

  if (error) return { status: 'error', errorMessage: error }
  if (!tempId) return { status: 'error', errorMessage: 'No authorization data received.' }
  return { status: 'done', errorMessage: null, tempId, email: params.get('email'), name: params.get('name') }
}

/**
 * This page is loaded in the OAuth popup after the provider redirects back.
 * The server redirects here with ?temp_id=...&email=...
 * We send the result back to the opener via BroadcastChannel, then close.
 */
export default function OAuthCallback() {
  // The query string never changes while this popup is open, so read it once.
  const [result] = useState(readCallbackParams)
  const { status, errorMessage } = result

  useEffect(() => {
    if (result.status !== 'done') return

    // Notify the opener via BroadcastChannel
    const channel = new BroadcastChannel('oauth-callback')
    channel.postMessage({ type: 'oauth-complete', tempId: result.tempId, email: result.email, name: result.name })
    channel.close()

    // Auto-close popup after a brief moment
    const timer = setTimeout(() => window.close(), 1500)
    return () => clearTimeout(timer)
  }, [result])

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-xl shadow-lg p-8 max-w-sm w-full text-center">
        {status === 'done' && (
          <>
            <div className="w-12 h-12 bg-green-100 rounded-full flex items-center justify-center mx-auto mb-3">
              <svg className="w-6 h-6 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <p className="text-gray-900 font-medium mb-1">Authorization successful!</p>
            <p className="text-gray-500 text-sm">This window will close automatically.</p>
          </>
        )}
        {status === 'error' && (
          <>
            <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center mx-auto mb-3">
              <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </div>
            <p className="text-gray-900 font-medium mb-1">Authorization failed</p>
            {errorMessage && (
              <p className="text-red-600 text-xs font-mono mt-2 max-w-xs break-all">{errorMessage}</p>
            )}
            <p className="text-gray-500 text-sm mt-2">Please close this window and try again.</p>
          </>
        )}
      </div>
    </div>
  )
}
