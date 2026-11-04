import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { ApiError, useSendFeedback, type FeedbackKind, type FeedbackSource } from '../hooks/useApi'
import { FEEDBACK_MAX, codePoints, coarseBrowser, newClientId } from '../lib/feedback'
import { FeedbackContext } from './feedbackContext'

const COUNTER_FROM = 1800

const KINDS: Array<{ value: FeedbackKind; label: string }> = [
  { value: 'bug', label: 'Bug' },
  { value: 'idea', label: 'Idea' },
  { value: 'other', label: 'Other' },
]

function screenName(pathname: string): string {
  const first = pathname.split('/').filter(Boolean)[0]
  if (!first) return 'Overview'
  return first.charAt(0).toUpperCase() + first.slice(1)
}

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const [source, setSource] = useState<FeedbackSource | null>(null)
  const openFeedback = useCallback((s: FeedbackSource = 'button') => {
    // Only one sheet at a time.
    setSource((current) => current ?? s)
  }, [])
  return (
    <FeedbackContext.Provider value={{ openFeedback }}>
      {children}
      {source && <FeedbackSheet source={source} onClose={() => setSource(null)} />}
    </FeedbackContext.Provider>
  )
}

type SendState = 'idle' | 'sending' | 'sent' | 'retry' | 'failed'

function FeedbackSheet({ source, onClose }: { source: FeedbackSource; onClose: () => void }) {
  const [kind, setKind] = useState<FeedbackKind>('bug')
  const [message, setMessage] = useState('')
  const [contact, setContact] = useState('')
  const [state, setState] = useState<SendState>('idle')
  const [invalidReason, setInvalidReason] = useState<string | null>(null)
  // One ID per sheet, reused on every retry so the relay never files the same note twice.
  const [clientId] = useState(newClientId)
  const send = useSendFeedback()
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    textareaRef.current?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  useEffect(() => {
    if (state !== 'sent') return
    const t = setTimeout(onClose, 1800)
    return () => clearTimeout(t)
  }, [state, onClose])

  const length = codePoints(message.trim())
  const tooLong = length > FEEDBACK_MAX
  const canSend = length > 0 && !tooLong && state !== 'sending' && state !== 'sent'

  const handleSend = () => {
    if (!canSend) return
    setState('sending')
    send.mutate(
      {
        kind,
        message: message.trim(),
        contact: contact.trim() || undefined,
        clientId,
        source,
        screen: screenName(window.location.pathname),
        language: navigator.language || undefined,
        browser: coarseBrowser(),
      },
      {
        onSuccess: () => setState('sent'),
        onError: (err) => {
          const status = err instanceof ApiError ? err.status : 0
          // A 400 names the field to fix (e.g. a mistyped email), so show it.
          setInvalidReason(status === 400 ? err.message : null)
          // 0 = the dashboard itself was unreachable; 503 = the relay asked us to try again.
          setState(status === 0 || status === 503 || status === 408 || status === 429 ? 'retry' : 'failed')
        },
      },
    )
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      onMouseDown={(e) => {
        // A stray click outside must not throw away a draft.
        if (e.target === e.currentTarget && message.trim() === '') onClose()
      }}
    >
      <div role="dialog" aria-modal="true" aria-labelledby="feedback-title" className="w-full max-w-lg rounded-lg bg-white p-5 shadow-xl">
        <div className="mb-4 flex items-center justify-between">
          <h2 id="feedback-title" className="text-lg font-semibold text-gray-900">Report an issue</h2>
          <button onClick={onClose} aria-label="Close" className="text-xl leading-none text-gray-400 hover:text-gray-600">
            &times;
          </button>
        </div>

        {state === 'sent' ? (
          <p className="py-6 text-center text-sm text-green-700">Thanks — we read every note.</p>
        ) : (
          <>
            <div className="mb-3 flex gap-2" role="radiogroup" aria-label="Kind">
              {KINDS.map((k) => (
                <button
                  key={k.value}
                  role="radio"
                  aria-checked={kind === k.value}
                  onClick={() => setKind(k.value)}
                  className={`rounded-full px-3 py-1 text-sm transition-colors ${
                    kind === k.value ? 'bg-blue-600 text-white' : 'bg-gray-100 text-gray-700 hover:bg-gray-200'
                  }`}
                >
                  {k.label}
                </button>
              ))}
            </div>

            <label htmlFor="feedback-message" className="sr-only">Message</label>
            <textarea
              id="feedback-message"
              ref={textareaRef}
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              rows={6}
              placeholder="What happened, or what would you like to see?"
              className="w-full resize-y rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
            />
            {length >= COUNTER_FROM && (
              <div className={`mt-1 text-right text-xs ${tooLong ? 'text-red-600' : 'text-gray-500'}`}>
                {length} / {FEEDBACK_MAX}
              </div>
            )}

            <label htmlFor="feedback-contact" className="mt-3 block text-sm text-gray-700">
              Email <span className="text-gray-400">(optional)</span>
            </label>
            <input
              id="feedback-contact"
              type="email"
              value={contact}
              onChange={(e) => setContact(e.target.value)}
              placeholder="Only if you'd like a reply"
              className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none"
            />

            <p className="mt-3 text-xs text-gray-500">
              We'll include your app version, operating system, browser and language. Notes, and your email if
              you add one, are posted to our public GitHub issue tracker.
            </p>

            {state === 'retry' && (
              <p className="mt-3 text-sm text-red-600">Couldn't send — check your connection and try again.</p>
            )}
            {state === 'failed' && (
              <p className="mt-3 text-sm text-red-600">
                {invalidReason ?? "Something went wrong and your feedback wasn't sent."}
              </p>
            )}

            <div className="mt-4 flex justify-end gap-2">
              <button onClick={onClose} className="rounded-lg px-4 py-2 text-sm text-gray-700 hover:bg-gray-100">
                Cancel
              </button>
              <button
                onClick={handleSend}
                disabled={!canSend}
                className="rounded-lg bg-blue-600 px-4 py-2 text-sm text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {state === 'sending' ? 'Sending…' : 'Send'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
