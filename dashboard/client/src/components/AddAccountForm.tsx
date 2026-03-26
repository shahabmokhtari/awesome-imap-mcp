import { useState, useEffect, useCallback } from 'react'
import {
  useCreateAccount,
  useOAuthProviders,
  useStartOAuth,
  useCompleteOAuth,
  type CreateAccountRequest,
} from '../hooks/useApi'

// ---------------------------------------------------------------------------
// Provider presets (single source of truth)
// ---------------------------------------------------------------------------

export const PROVIDER_PRESETS: Record<string, Partial<CreateAccountRequest>> = {
  gmail: { imapHost: 'imap.gmail.com', imapPort: 993, smtpHost: 'smtp.gmail.com', smtpPort: 465, smtpUseSsl: true },
  outlook: { imapHost: 'outlook.office365.com', imapPort: 993, smtpHost: 'smtp.office365.com', smtpPort: 587, smtpUseSsl: false },
  yahoo: { imapHost: 'imap.mail.yahoo.com', imapPort: 993, smtpHost: 'smtp.mail.yahoo.com', smtpPort: 465, smtpUseSsl: true },
  icloud: { imapHost: 'imap.mail.me.com', imapPort: 993, smtpHost: 'smtp.mail.me.com', smtpPort: 587, smtpUseSsl: false },
  zoho: { imapHost: 'imap.zoho.com', imapPort: 993, smtpHost: 'smtp.zoho.com', smtpPort: 465, smtpUseSsl: true },
  protonmail: { imapHost: '127.0.0.1', imapPort: 1143, smtpHost: '127.0.0.1', smtpPort: 1025, smtpUseSsl: false },
  generic: {},
}

export const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  gmail: 'Gmail',
  outlook: 'Outlook',
  yahoo: 'Yahoo',
  icloud: 'iCloud',
  zoho: 'Zoho',
  protonmail: 'ProtonMail',
  generic: 'Other',
}

export const PROVIDER_HELP_LINKS: Record<string, { label: string; url: string }> = {
  gmail: { label: 'Get a Gmail App Password', url: 'https://support.google.com/accounts/answer/185833' },
  outlook: { label: 'Create an Outlook App Password', url: 'https://support.microsoft.com/en-us/account-billing/manage-app-passwords-for-two-step-verification-d6dc8c6d-4bf7-4851-ad95-6d07799205e9' },
  yahoo: { label: 'Generate a Yahoo App Password', url: 'https://help.yahoo.com/kb/generate-manage-third-party-passwords-sln15241.html' },
  icloud: { label: 'Generate an iCloud App-Specific Password', url: 'https://support.apple.com/en-us/102654' },
  zoho: { label: 'Generate a Zoho App-Specific Password', url: 'https://www.zoho.com/mail/help/adminconsole/two-factor-authentication.html' },
  protonmail: { label: 'Set up ProtonMail Bridge', url: 'https://proton.me/mail/bridge' },
}

export const PROVIDER_NOTES: Record<string, string> = {
  protonmail: 'Requires ProtonMail Bridge running locally.',
}

export const PROVIDER_LIST = Object.keys(PROVIDER_PRESETS)

function ProviderIcon({ provider, size = 36 }: { provider: string; size?: number }) {
  const s = size
  const icons: Record<string, React.ReactNode> = {
    // Google "G" multicolor
    gmail: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <path d="M43.611 20.083H42V20H24v8h11.303c-1.649 4.657-6.08 8-11.303 8-6.627 0-12-5.373-12-12s5.373-12 12-12c3.059 0 5.842 1.154 7.961 3.039l5.657-5.657C34.046 6.053 29.268 4 24 4 12.955 4 4 12.955 4 24s8.955 20 20 20 20-8.955 20-20c0-1.341-.138-2.65-.389-3.917z" fill="#FFC107"/>
        <path d="M6.306 14.691l6.571 4.819C14.655 15.108 18.961 12 24 12c3.059 0 5.842 1.154 7.961 3.039l5.657-5.657C34.046 6.053 29.268 4 24 4 16.318 4 9.656 8.337 6.306 14.691z" fill="#FF3D00"/>
        <path d="M24 44c5.166 0 9.86-1.977 13.409-5.192l-6.19-5.238A11.91 11.91 0 0124 36c-5.202 0-9.619-3.317-11.283-7.946l-6.522 5.025C9.505 39.556 16.227 44 24 44z" fill="#4CAF50"/>
        <path d="M43.611 20.083H42V20H24v8h11.303a12.04 12.04 0 01-4.087 5.571l6.19 5.238C36.971 39.205 44 34 44 24c0-1.341-.138-2.65-.389-3.917z" fill="#1976D2"/>
      </svg>
    ),
    // Microsoft 4-pane window
    outlook: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <path d="M6 6h17v17H6z" fill="#F25022"/>
        <path d="M25 6h17v17H25z" fill="#7FBA00"/>
        <path d="M6 25h17v17H6z" fill="#00A4EF"/>
        <path d="M25 25h17v17H25z" fill="#FFB900"/>
      </svg>
    ),
    // Yahoo purple "Y!"
    yahoo: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <circle cx="24" cy="24" r="22" fill="#5F01D1"/>
        <text x="24" y="26" textAnchor="middle" dominantBaseline="central" fill="#fff" fontSize="22" fontWeight="bold" fontFamily="system-ui">Y!</text>
      </svg>
    ),
    // iCloud cloud shape
    icloud: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <path d="M38.5 32h-27A7.5 7.5 0 014 24.5a7.5 7.5 0 016.08-7.36A11 11 0 0121 8a11 11 0 0110.92 9.64A8.5 8.5 0 0140 26a8.49 8.49 0 01-1.5 6z" fill="#3693F3"/>
      </svg>
    ),
    // Zoho "Z"
    zoho: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <rect x="4" y="4" width="40" height="40" rx="8" fill="#E42527"/>
        <text x="24" y="26" textAnchor="middle" dominantBaseline="central" fill="#fff" fontSize="26" fontWeight="bold" fontFamily="system-ui">Z</text>
      </svg>
    ),
    // ProtonMail shield
    protonmail: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <path d="M24 4L6 12v12c0 11 7.7 21.3 18 24 10.3-2.7 18-13 18-24V12L24 4z" fill="#6D4AFF"/>
        <path d="M14 20l4 4 4-4h8v12H14V20z" fill="#fff" opacity=".9"/>
        <path d="M14 20l10 7 10-7" fill="none" stroke="#fff" strokeWidth="2"/>
      </svg>
    ),
    // Generic envelope
    generic: (
      <svg width={s} height={s} viewBox="0 0 48 48">
        <rect x="4" y="10" width="40" height="28" rx="4" fill="#6B7280"/>
        <path d="M4 14l20 13 20-13" fill="none" stroke="#fff" strokeWidth="2.5"/>
      </svg>
    ),
  }
  return icons[provider] ?? icons.generic
}

const OAUTH_SIGN_IN_LABELS: Record<string, string> = {
  gmail: 'Sign in with Google',
  outlook: 'Sign in with Microsoft',
  zoho: 'Sign in with Zoho',
}

function emptyForm(): CreateAccountRequest {
  return {
    name: '',
    imapHost: '',
    imapPort: 993,
    smtpHost: '',
    smtpPort: 465,
    smtpUseSsl: true,
    username: '',
    authType: 'app_password',
    password: '',
    provider: 'generic',
  }
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface AddAccountFormProps {
  onComplete: () => void   // called after account is successfully created
  onCancel?: () => void    // called when user cancels (optional, hides cancel button if not provided)
}

// ---------------------------------------------------------------------------
// AddAccountForm component
// ---------------------------------------------------------------------------

export default function AddAccountForm({ onComplete, onCancel }: AddAccountFormProps) {
  const [selectedProvider, setSelectedProvider] = useState<string | null>(null)
  const [form, setForm] = useState<CreateAccountRequest>(emptyForm())
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [saving, setSaving] = useState(false)
  const [oauthPending, setOauthPending] = useState(false)
  const [oauthTempId, setOauthTempId] = useState<string | null>(null)
  const [oauthEmail, setOauthEmail] = useState<string | null>(null)
  const [oauthName, setOauthName] = useState<string | null>(null)

  const createAccount = useCreateAccount()
  const { data: oauthProviders } = useOAuthProviders()
  const startOAuth = useStartOAuth()
  const completeOAuth = useCompleteOAuth()

  const set = <K extends keyof CreateAccountRequest>(key: K, value: CreateAccountRequest[K]) => {
    setForm(prev => ({ ...prev, [key]: value }))
    setTestResult(null)
  }

  const handleProviderSelect = (provider: string) => {
    setSelectedProvider(provider)
    const preset = PROVIDER_PRESETS[provider] ?? {}
    setForm({ ...emptyForm(), provider, ...preset })
    setTestResult(null)
    setOauthTempId(null)
    setOauthEmail(null)
  }

  // Listen for OAuth callback via BroadcastChannel
  const handleOAuthMessage = useCallback((event: MessageEvent) => {
    if (event.data?.type === 'oauth-complete' && event.data.tempId) {
      setOauthTempId(event.data.tempId)
      setOauthEmail(event.data.email || null)
      setOauthName(event.data.name || null)
      setOauthPending(false)
      // Pre-fill account name: "Name (email) [Provider]"
      const userName = event.data.name || ''
      const userEmail = event.data.email || ''
      setForm(prev => {
        const pLabel = PROVIDER_DISPLAY_NAMES[prev.provider] ?? prev.provider
        const suggested = userName && userEmail
          ? `${userName} (${userEmail}) [${pLabel}]`
          : userEmail
            ? `${userEmail} [${pLabel}]`
            : userName
              ? `${userName} [${pLabel}]`
              : ''
        return { ...prev, name: prev.name || suggested }
      })
    }
  }, [])

  useEffect(() => {
    const channel = new BroadcastChannel('oauth-callback')
    channel.addEventListener('message', handleOAuthMessage)
    return () => { channel.close() }
  }, [handleOAuthMessage])

  const handleOAuthSignIn = async () => {
    if (!selectedProvider) return
    setOauthPending(true)
    setTestResult(null)
    try {
      const result = await startOAuth.mutateAsync({ provider: selectedProvider })
      const popup = window.open(result.auth_url, 'oauth-popup', 'width=600,height=700,popup=yes')
      if (popup) {
        const timer = setInterval(() => {
          if (popup.closed) {
            clearInterval(timer)
            setOauthPending(prev => {
              if (prev && !oauthTempId) return false
              return prev
            })
          }
        }, 500)
      }
    } catch (err) {
      setOauthPending(false)
      setTestResult({ success: false, message: err instanceof Error ? err.message : 'Failed to start OAuth' })
    }
  }

  const handleOAuthComplete = () => {
    if (!oauthTempId || !selectedProvider) return
    const providerLabel = PROVIDER_DISPLAY_NAMES[selectedProvider] ?? selectedProvider
    const defaultName = oauthName && oauthEmail
      ? `${oauthName} (${oauthEmail}) [${providerLabel}]`
      : oauthEmail
        ? `${oauthEmail} [${providerLabel}]`
        : `${providerLabel} account`
    const name = form.name.trim() || defaultName
    const email = oauthEmail || form.username.trim() || undefined
    completeOAuth.mutate({ tempId: oauthTempId, name, email }, {
      onSuccess: () => onComplete(),
      onError: (err) => setTestResult({ success: false, message: err.message }),
    })
  }

  const providerHasOAuth = selectedProvider ? oauthProviders?.[selectedProvider]?.configured === true : false
  const isValid = form.name.trim() && form.username.trim() && (form.password ?? '').trim() && form.imapHost.trim()

  const handleTestAndSave = async () => {
    setSaving(true)
    setTestResult(null)
    try {
      const result = await createAccount.mutateAsync(form)
      const accountId = result.id
      const token = localStorage.getItem('dashboard_token')
      const testRes = await fetch(`/api/accounts/${accountId}/test`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
      })
      const testData = await testRes.json().catch(() => ({ success: false, message: 'Unknown error' }))
      if (testData.success) {
        setTestResult({ success: true, message: testData.message || 'Connection successful!' })
        setTimeout(() => onComplete(), 1500)
      } else {
        setTestResult({ success: false, message: testData.message || 'Connection test failed. Account was saved anyway.' })
        setTimeout(() => onComplete(), 3000)
      }
    } catch (err) {
      setTestResult({ success: false, message: err instanceof Error ? err.message : 'Failed to create account' })
    } finally {
      setSaving(false)
    }
  }

  const handleSaveOnly = () => {
    createAccount.mutate(form, {
      onSuccess: () => onComplete(),
      onError: (err) => setTestResult({ success: false, message: err.message }),
    })
  }

  const helpLink = PROVIDER_HELP_LINKS[form.provider]

  // ---------------------------------------------------------------------------
  // Provider selection cards
  // ---------------------------------------------------------------------------

  if (!selectedProvider) {
    return (
      <div>
        <h3 className="text-xl font-bold text-gray-900 mb-2">Add an Account</h3>
        <p className="text-gray-500 mb-6">Select your email provider to get started.</p>

        <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-6">
          {PROVIDER_LIST.map(provider => (
            <button
              key={provider}
              onClick={() => handleProviderSelect(provider)}
              className="flex flex-col items-center justify-center p-4 bg-white border-2 border-gray-200 rounded-xl hover:border-blue-500 hover:bg-blue-50 transition-colors text-center gap-2"
            >
              <ProviderIcon provider={provider} size={36} />
              <span className="text-sm font-medium text-gray-900">{PROVIDER_DISPLAY_NAMES[provider] ?? provider}</span>
              {oauthProviders?.[provider]?.configured && (
                <span className="text-xs text-green-600">OAuth</span>
              )}
            </button>
          ))}
        </div>

        {onCancel && (
          <button
            onClick={onCancel}
            className="text-sm text-gray-500 hover:text-gray-700"
          >
            Cancel
          </button>
        )}
      </div>
    )
  }

  // ---------------------------------------------------------------------------
  // Account form (after provider is selected)
  // ---------------------------------------------------------------------------

  return (
    <div>
      <h3 className="text-xl font-bold text-gray-900 mb-2">Add an Account</h3>
      <div className="flex items-center gap-2 mb-6">
        <span className="text-gray-500">Provider:</span>
        <span className="text-sm font-medium text-gray-900 capitalize bg-blue-50 px-2 py-0.5 rounded">
          {selectedProvider}
        </span>
        <button
          onClick={() => setSelectedProvider(null)}
          className="text-xs text-blue-600 hover:text-blue-800"
        >
          Change
        </button>
      </div>

      {/* Provider-specific note */}
      {PROVIDER_NOTES[selectedProvider] && (
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3 mb-5 text-sm text-amber-800">
          {PROVIDER_NOTES[selectedProvider]}
        </div>
      )}

      {/* OAuth sign-in (shown when provider supports it) */}
      {providerHasOAuth && (
        <div className="border border-gray-200 rounded-lg p-4 bg-gray-50 mb-5">
          {!oauthTempId ? (
            <div className="space-y-3">
              <button
                onClick={handleOAuthSignIn}
                disabled={oauthPending}
                className="w-full px-4 py-2.5 bg-white border border-gray-300 rounded-lg text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors disabled:opacity-50 flex items-center justify-center gap-2"
              >
                {oauthPending ? 'Waiting for authorization...' : OAUTH_SIGN_IN_LABELS[selectedProvider] || `Sign in with ${selectedProvider}`}
              </button>
              {oauthPending && (
                <p className="text-xs text-gray-500 text-center">
                  Complete the sign-in in the popup window. This page will update automatically.
                </p>
              )}
              <div className="relative">
                <div className="absolute inset-0 flex items-center"><div className="w-full border-t border-gray-200" /></div>
                <div className="relative flex justify-center text-xs"><span className="bg-gray-50 px-2 text-gray-400">or use app password below</span></div>
              </div>
            </div>
          ) : (
            <div className="space-y-3">
              <div className="bg-green-50 border border-green-200 rounded-lg p-3 text-sm text-green-700">
                Authorized as <strong>{oauthName || oauthEmail || 'verified user'}</strong>{oauthName && oauthEmail ? ` (${oauthEmail})` : ''}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Account Name</label>
                <input
                  type="text"
                  placeholder="e.g. Personal Email"
                  value={form.name}
                  onChange={e => set('name', e.target.value)}
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                />
              </div>
              <button
                onClick={handleOAuthComplete}
                disabled={completeOAuth.isPending}
                className="px-5 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {completeOAuth.isPending ? 'Saving...' : 'Save Account'}
              </button>
            </div>
          )}
        </div>
      )}

      {/* App password form (shown when OAuth not completed) */}
      {!oauthTempId && (
        <>
          {helpLink && (
            <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 mb-5">
              <a
                href={helpLink.url}
                target="_blank"
                rel="noopener noreferrer"
                className="text-sm text-blue-700 hover:text-blue-900"
              >
                {helpLink.label} &rarr;
              </a>
            </div>
          )}

          <div className="space-y-4 max-w-lg">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Account Name</label>
              <input
                type="text"
                placeholder="e.g. Personal Email"
                value={form.name}
                onChange={e => set('name', e.target.value)}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Username / Email</label>
              <input
                type="text"
                placeholder="you@example.com"
                value={form.username}
                onChange={e => set('username', e.target.value)}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Password (App Password)</label>
              <input
                type="password"
                placeholder="App password"
                value={form.password ?? ''}
                onChange={e => set('password', e.target.value)}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              />
            </div>

            {/* Server details (collapsible for preset providers) */}
            <details className="border border-gray-200 rounded-lg" open={selectedProvider === 'generic'}>
              <summary className="px-4 py-2 text-sm font-medium text-gray-700 cursor-pointer hover:bg-gray-50 select-none">
                Server Settings
                {selectedProvider !== 'generic' && (
                  <span className="text-xs text-gray-400 ml-2">(auto-filled from provider)</span>
                )}
              </summary>
              <div className="px-4 pb-4 pt-2 space-y-3">
                <div className="grid grid-cols-3 gap-3">
                  <div className="col-span-2">
                    <label className="block text-xs text-gray-500 mb-1">IMAP Host</label>
                    <input type="text" value={form.imapHost} onChange={e => set('imapHost', e.target.value)} className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500" />
                  </div>
                  <div>
                    <label className="block text-xs text-gray-500 mb-1">Port</label>
                    <input type="number" value={form.imapPort} onChange={e => set('imapPort', Number(e.target.value))} className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500" />
                  </div>
                </div>
                <div className="grid grid-cols-3 gap-3">
                  <div className="col-span-2">
                    <label className="block text-xs text-gray-500 mb-1">SMTP Host</label>
                    <input type="text" value={form.smtpHost ?? ''} onChange={e => set('smtpHost', e.target.value)} className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500" />
                  </div>
                  <div>
                    <label className="block text-xs text-gray-500 mb-1">Port</label>
                    <input type="number" value={form.smtpPort} onChange={e => set('smtpPort', Number(e.target.value))} className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500" />
                  </div>
                </div>
                <label className="inline-flex items-center cursor-pointer">
                  <input type="checkbox" checked={form.smtpUseSsl} onChange={e => set('smtpUseSsl', e.target.checked)} className="sr-only peer" />
                  <div className="w-9 h-5 bg-gray-300 peer-focus:outline-none peer-focus:ring-2 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:bg-blue-600 relative" />
                  <span className="ml-3 text-sm text-gray-700">SMTP SSL/TLS</span>
                </label>
              </div>
            </details>

            {/* Test result */}
            {testResult && (
              <div className={`rounded-lg p-3 text-sm ${testResult.success ? 'bg-green-50 border border-green-200 text-green-700' : 'bg-red-50 border border-red-200 text-red-700'}`}>
                {testResult.message}
              </div>
            )}

            {createAccount.isError && !testResult && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
                {createAccount.error.message}
              </div>
            )}

            <div className="flex items-center gap-3 pt-2">
              <button onClick={handleTestAndSave} disabled={!isValid || saving} className="px-5 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed">
                {saving ? 'Testing...' : 'Test & Save'}
              </button>
              <button onClick={handleSaveOnly} disabled={!isValid || createAccount.isPending} className="px-5 py-2 text-gray-700 bg-gray-100 rounded-lg text-sm hover:bg-gray-200 transition-colors disabled:opacity-50 disabled:cursor-not-allowed">
                {createAccount.isPending ? 'Saving...' : 'Save without testing'}
              </button>
            </div>
          </div>
        </>
      )}

      {onCancel && (
        <div className="mt-4 pt-4 border-t border-gray-100">
          <button onClick={onCancel} className="text-sm text-gray-500 hover:text-gray-700">
            Cancel
          </button>
        </div>
      )}
    </div>
  )
}
