import { useState, useMemo, useEffect, useCallback } from 'react'
import {
  useAccounts,
  useFolders,
  useMessages,
  useMessage,
  useSearchMessages,
  useFetchBody,
  useClearFolderCache,
  useExecuteTool,
  type MessageSummary,
  type FolderInfo,
} from '../hooks/useApi'
import { sanitizeEmailHtml } from '../lib/sanitizeEmail'

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatDate(dateEpoch: number | null, dateStr: string): string {
  if (dateEpoch == null) return dateStr || ''
  const d = new Date(dateEpoch * 1000)
  const now = new Date()
  const isToday =
    d.getFullYear() === now.getFullYear() &&
    d.getMonth() === now.getMonth() &&
    d.getDate() === now.getDate()
  if (isToday) {
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  }
  const isThisYear = d.getFullYear() === now.getFullYear()
  if (isThisYear) {
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' })
  }
  return d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' })
}

function isUnread(flags: string): boolean {
  // Messages are unread if they do NOT have the \Seen flag
  return !flags.toLowerCase().includes('\\seen')
}

/** Extract custom labels/keywords from flags (exclude standard IMAP flags). */
function getLabels(flags: string): string[] {
  if (!flags) return []
  const standard = new Set(['\\seen', '\\flagged', '\\answered', '\\deleted', '\\draft', '\\recent', '$forwarded', '$mdnsent'])
  return flags.split(' ').filter(f => f && !standard.has(f.toLowerCase()))
}

const LABEL_COLORS = [
  'bg-purple-100 text-purple-700',
  'bg-teal-100 text-teal-700',
  'bg-orange-100 text-orange-700',
  'bg-pink-100 text-pink-700',
  'bg-cyan-100 text-cyan-700',
  'bg-lime-100 text-lime-700',
]

function labelColor(label: string): string {
  let hash = 0
  for (let i = 0; i < label.length; i++) hash = ((hash << 5) - hash + label.charCodeAt(i)) | 0
  return LABEL_COLORS[Math.abs(hash) % LABEL_COLORS.length]
}

function folderIcon(role: string | null, path: string): string {
  const lower = (role ?? path).toLowerCase()
  if (lower.includes('inbox')) return '\u{1F4E5}'
  if (lower.includes('sent')) return '\u{1F4E4}'
  if (lower.includes('draft')) return '\u{1F4DD}'
  if (lower.includes('trash') || lower.includes('deleted')) return '\u{1F5D1}'
  if (lower.includes('spam') || lower.includes('junk')) return '\u{26A0}'
  if (lower.includes('archive')) return '\u{1F4E6}'
  if (lower.includes('starred') || lower.includes('flagged')) return '\u{2B50}'
  return '\u{1F4C1}'
}

// ---------------------------------------------------------------------------
// Folder sidebar
// ---------------------------------------------------------------------------

function FolderList({
  folders,
  selectedFolderId,
  onSelect,
}: {
  folders: FolderInfo[]
  selectedFolderId: number | undefined
  onSelect: (id: number) => void
}) {
  return (
    <ul className="space-y-0.5">
      {folders.map((f) => {
        const active = f.id === selectedFolderId
        return (
          <li key={f.id}>
            <button
              onClick={() => onSelect(f.id)}
              className={`w-full text-left px-3 py-2 rounded-lg text-sm flex items-center gap-2 transition-colors ${
                active
                  ? 'bg-blue-50 text-blue-700 font-medium'
                  : 'text-gray-700 hover:bg-gray-100'
              }`}
            >
              <span className="flex-shrink-0 text-xs">{folderIcon(f.role, f.path)}</span>
              <span className="flex-1 truncate">{f.displayName}</span>
              {f.unreadCount > 0 && (
                <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium ${
                  active ? 'bg-blue-200 text-blue-800' : 'bg-gray-200 text-gray-600'
                }`}>
                  {f.unreadCount}
                </span>
              )}
              {f.messageCount > 0 && f.unreadCount === 0 && (
                <span className="text-xs text-gray-400">{f.messageCount}</span>
              )}
            </button>
          </li>
        )
      })}
    </ul>
  )
}

// ---------------------------------------------------------------------------
// Message row in the list
// ---------------------------------------------------------------------------

function MessageRow({
  msg,
  isSelected,
  onClick,
}: {
  msg: MessageSummary
  isSelected: boolean
  onClick: () => void
}) {
  const unread = isUnread(msg.flags)
  const dateFormatted = formatDate(msg.dateEpoch, msg.date)

  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-4 py-3 border-b border-gray-100 last:border-0 transition-colors ${
        isSelected ? 'bg-blue-50' : 'hover:bg-gray-50'
      }`}
    >
      <div className="flex items-start gap-3">
        {/* Unread indicator */}
        <div className="mt-1.5 flex-shrink-0">
          {unread ? (
            <span className="block w-2 h-2 rounded-full bg-blue-500" />
          ) : (
            <span className="block w-2 h-2" />
          )}
        </div>

        <div className="flex-1 min-w-0">
          <div className="flex items-center justify-between gap-2">
            <span className={`text-sm truncate ${unread ? 'font-semibold text-gray-900' : 'text-gray-700'}`}>
              {msg.fromAddress || msg.fromEmail || '(unknown sender)'}
            </span>
            <span className="text-xs text-gray-400 flex-shrink-0">{dateFormatted}</span>
          </div>
          <div className={`text-sm truncate mt-0.5 ${unread ? 'font-medium text-gray-900' : 'text-gray-600'}`}>
            {msg.hasAttachments && <span className="mr-1 text-gray-400" title="Has attachments">{'\u{1F4CE}'}</span>}
            {msg.bodyFetched && <span className="mr-1 text-green-400" title="Body cached">{'\u{2709}'}</span>}
            {msg.subject}
          </div>
          <div className="flex items-center gap-1 mt-0.5">
            {msg.snippet && (
              <span className="text-xs text-gray-400 truncate">{msg.snippet}</span>
            )}
            {getLabels(msg.flags).map(label => (
              <span key={label} className={`inline-block px-1.5 py-0 rounded text-[10px] font-medium flex-shrink-0 ${labelColor(label)}`}>
                {label}
              </span>
            ))}
          </div>
        </div>
      </div>
    </button>
  )
}

// ---------------------------------------------------------------------------
// Message detail view
// ---------------------------------------------------------------------------

function MessageView({
  accountId,
  folderId,
  folderPath,
  uid,
  onClose,
}: {
  accountId: string
  folderId: number
  folderPath: string
  uid: number
  onClose: () => void
}) {
  const { data: msg, isLoading, error } = useMessage(accountId, folderId, uid)
  const fetchBody = useFetchBody()
  const executeTool = useExecuteTool()
  const [showHtml, setShowHtml] = useState(false)
  const [allowRemoteImages, setAllowRemoteImages] = useState(false)
  const [showHeaders, setShowHeaders] = useState(false)
  const [actionStatus, setActionStatus] = useState<string | null>(null)

  useEffect(() => {
    setAllowRemoteImages(false)
    setShowHtml(false)
    setShowHeaders(false)
    setActionStatus(null)
  }, [uid])

  const handleMarkRead = () => {
    setActionStatus('Queuing mark as read...')
    executeTool.mutate(
      { name: 'mark_read', args: { accountId, uids: String(uid), folder: folderPath } },
      {
        onSuccess: () => setActionStatus('Queued: mark as read'),
        onError: (err) => setActionStatus(`Error: ${err.message}`),
      }
    )
  }

  const handleMarkUnread = () => {
    setActionStatus('Queuing mark as unread...')
    executeTool.mutate(
      { name: 'mark_unread', args: { accountId, uids: String(uid), folder: folderPath } },
      {
        onSuccess: () => setActionStatus('Queued: mark as unread'),
        onError: (err) => setActionStatus(`Error: ${err.message}`),
      }
    )
  }

  const handleDelete = () => {
    if (!window.confirm('Delete this message? It will be moved to trash on most IMAP servers.')) return
    setActionStatus('Queuing delete...')
    executeTool.mutate(
      { name: 'delete_messages', args: { accountId, uids: String(uid), folder: folderPath } },
      {
        onSuccess: () => setActionStatus('Queued: delete'),
        onError: (err) => setActionStatus(`Error: ${err.message}`),
      }
    )
  }

  const handleOpenInNewWindow = () => {
    if (!msg) return
    const newWindow = window.open('', '_blank')
    if (!newWindow) return
    const escapedSubject = (msg.subject || '(no subject)').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    const escapedFrom = (msg.fromAddress || msg.fromEmail || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    const dateStr = msg.dateEpoch ? new Date(msg.dateEpoch * 1000).toLocaleString() : msg.date
    const content = msg.bodyHtml
      ? sanitizeEmailHtml(msg.bodyHtml, true)
      : `<pre style="font-family: sans-serif; white-space: pre-wrap;">${(msg.bodyText || 'No content').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')}</pre>`
    newWindow.document.write(`<!DOCTYPE html>
<html>
<head><title>${escapedSubject}</title></head>
<body style="margin: 20px; font-family: system-ui, -apple-system, sans-serif;">
  <h2 style="margin-bottom: 4px;">${escapedSubject}</h2>
  <p style="color: #666; margin-top: 0;">From: ${escapedFrom} | Date: ${dateStr}</p>
  <hr style="border: none; border-top: 1px solid #ddd;"/>
  ${content}
</body>
</html>`)
    newWindow.document.close()
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full text-gray-400 text-sm">
        Loading message...
      </div>
    )
  }

  if (error || !msg) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-2">
        <p className="text-red-500 text-sm">{error?.message ?? 'Message not found'}</p>
        <button onClick={onClose} className="text-sm text-blue-600 hover:text-blue-800">
          Go back
        </button>
      </div>
    )
  }

  const hasHtml = !!msg.bodyHtml
  const hasText = !!msg.bodyText
  const msgIsUnread = isUnread(msg.flags)

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <div className="px-6 py-4 border-b border-gray-200 flex-shrink-0">
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0 flex-1">
            <h3 className="text-lg font-semibold text-gray-900 break-words">{msg.subject}</h3>
            <div className="mt-2 space-y-1">
              <div className="flex items-center gap-2 text-sm">
                <span className="text-gray-500 w-12 flex-shrink-0">From</span>
                <span className="text-gray-900 font-medium">{msg.fromAddress || msg.fromEmail}</span>
                {msg.fromEmail && msg.fromAddress && msg.fromEmail !== msg.fromAddress && (
                  <span className="text-gray-400">&lt;{msg.fromEmail}&gt;</span>
                )}
              </div>
              {msg.toAddresses && (
                <div className="flex items-start gap-2 text-sm">
                  <span className="text-gray-500 w-12 flex-shrink-0">To</span>
                  <span className="text-gray-700 break-words">{msg.toAddresses}</span>
                </div>
              )}
              {msg.ccAddresses && (
                <div className="flex items-start gap-2 text-sm">
                  <span className="text-gray-500 w-12 flex-shrink-0">Cc</span>
                  <span className="text-gray-700 break-words">{msg.ccAddresses}</span>
                </div>
              )}
              <div className="flex items-center gap-2 text-sm">
                <span className="text-gray-500 w-12 flex-shrink-0">Date</span>
                <span className="text-gray-700">
                  {msg.dateEpoch
                    ? new Date(msg.dateEpoch * 1000).toLocaleString()
                    : msg.date}
                </span>
              </div>
            </div>
          </div>

          {/* Action buttons */}
          <div className="flex items-center gap-1 flex-shrink-0">
            {/* Mark Read / Unread toggle */}
            {msgIsUnread ? (
              <button
                onClick={handleMarkRead}
                disabled={executeTool.isPending}
                title="Mark as read"
                className="p-1.5 rounded hover:bg-gray-100 text-gray-500 hover:text-gray-700 disabled:opacity-50"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 19V5a2 2 0 012-2h14a2 2 0 012 2v14M3 19l6.75-4.5M21 19l-6.75-4.5M3 5l9 6 9-6" />
                </svg>
              </button>
            ) : (
              <button
                onClick={handleMarkUnread}
                disabled={executeTool.isPending}
                title="Mark as unread"
                className="p-1.5 rounded hover:bg-gray-100 text-gray-500 hover:text-gray-700 disabled:opacity-50"
              >
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l9 6 9-6M3 8v10a2 2 0 002 2h14a2 2 0 002-2V8M3 8l9-4 9 4" />
                </svg>
              </button>
            )}

            {/* Delete */}
            <button
              onClick={handleDelete}
              disabled={executeTool.isPending}
              title="Delete"
              className="p-1.5 rounded hover:bg-gray-100 text-gray-500 hover:text-red-600 disabled:opacity-50"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
              </svg>
            </button>

            {/* Show Headers */}
            <button
              onClick={() => setShowHeaders(!showHeaders)}
              title={showHeaders ? 'Hide headers' : 'Show headers'}
              className={`p-1.5 rounded hover:bg-gray-100 disabled:opacity-50 ${showHeaders ? 'text-blue-600 bg-blue-50' : 'text-gray-500 hover:text-gray-700'}`}
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4" />
              </svg>
            </button>

            {/* Open in New Window */}
            <button
              onClick={handleOpenInNewWindow}
              title="Open in new window"
              className="p-1.5 rounded hover:bg-gray-100 text-gray-500 hover:text-gray-700"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
              </svg>
            </button>

            {/* Close */}
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600 p-1.5"
              title="Close"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
        </div>

        {/* Action status feedback */}
        {actionStatus && (
          <div className={`mt-2 text-xs px-2 py-1 rounded ${actionStatus.startsWith('Error') ? 'bg-red-50 text-red-600' : 'bg-green-50 text-green-700'}`}>
            {actionStatus}
          </div>
        )}

        {/* View toggle if both text and html are available */}
        {hasText && hasHtml && (
          <div className="mt-3 flex gap-2">
            <button
              onClick={() => setShowHtml(false)}
              className={`px-3 py-1 rounded text-xs font-medium transition-colors ${
                !showHtml ? 'bg-gray-900 text-white' : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
              }`}
            >
              Plain Text
            </button>
            <button
              onClick={() => setShowHtml(true)}
              className={`px-3 py-1 rounded text-xs font-medium transition-colors ${
                showHtml ? 'bg-gray-900 text-white' : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
              }`}
            >
              HTML
            </button>
          </div>
        )}
      </div>

      {/* Body */}
      <div className="flex-1 overflow-auto p-6">
        {/* Headers section */}
        {showHeaders && (
          <div className="mb-4 space-y-3">
            {/* Structured headers — always available from sync metadata */}
            <div className="text-xs bg-gray-50 p-4 rounded border border-gray-200 space-y-1.5">
              <div className="font-medium text-gray-700 mb-2">Message Headers</div>
              {msg.messageId && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">Message-ID:</span> <span className="font-mono break-all">{msg.messageId}</span></div>
              )}
              {msg.threadId && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">Thread-ID:</span> <span className="font-mono break-all">{msg.threadId}</span></div>
              )}
              {msg.inReplyTo && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">In-Reply-To:</span> <span className="font-mono break-all">{msg.inReplyTo}</span></div>
              )}
              {msg.referencesHdr && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">References:</span> <span className="font-mono break-all">{msg.referencesHdr}</span></div>
              )}
              {msg.toAddresses && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">To:</span> <span className="break-all">{msg.toAddresses}</span></div>
              )}
              {msg.ccAddresses && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">Cc:</span> <span className="break-all">{msg.ccAddresses}</span></div>
              )}
              {msg.sizeBytes != null && (
                <div><span className="font-mono text-gray-500 w-28 inline-block">Size:</span> {(msg.sizeBytes / 1024).toFixed(1)} KB</div>
              )}
              <div><span className="font-mono text-gray-500 w-28 inline-block">UID:</span> {msg.uid}</div>
              <div><span className="font-mono text-gray-500 w-28 inline-block">Flags:</span> <span className="font-mono">{msg.flags || '(none)'}</span></div>
            </div>
            {/* Raw headers — available after body fetch */}
            {msg.rawHeaders && (
              <details className="text-xs">
                <summary className="cursor-pointer text-gray-500 hover:text-gray-700 font-medium">Raw Headers</summary>
                <pre className="mt-2 font-mono bg-gray-50 p-4 rounded border border-gray-200 overflow-auto max-h-64 whitespace-pre-wrap break-all">
                  {msg.rawHeaders}
                </pre>
              </details>
            )}
          </div>
        )}

        {!msg.bodyFetched && !hasText && !hasHtml ? (
          <div className="text-gray-400 text-sm text-center py-8">
            <p className="mb-3">Message body has not been fetched yet.</p>
            <button
              onClick={() => fetchBody.mutate({ accountId, folderId, uid: msg.uid })}
              disabled={fetchBody.isPending}
              className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {fetchBody.isPending ? 'Fetching...' : 'Fetch Body'}
            </button>
            {fetchBody.error && (
              <p className="mt-2 text-red-500 text-xs">{fetchBody.error.message}</p>
            )}
          </div>
        ) : hasHtml && (showHtml || !hasText) ? (
          <div className="h-full flex flex-col">
            {!allowRemoteImages && (
              <div className="flex items-center gap-2 px-3 py-1.5 bg-amber-50 border-b border-amber-200 text-xs text-amber-700 flex-shrink-0">
                <span>Remote images are blocked.</span>
                <button
                  onClick={() => setAllowRemoteImages(true)}
                  className="underline hover:text-amber-900"
                >
                  Load remote images
                </button>
              </div>
            )}
            <iframe
              srcDoc={sanitizeEmailHtml(msg.bodyHtml!, allowRemoteImages)}
              title="Email body"
              className="w-full flex-1 border-0 min-h-[400px] rounded bg-white"
              sandbox=""
            />
          </div>
        ) : hasText ? (
          <pre className="text-sm text-gray-800 whitespace-pre-wrap break-words font-sans leading-relaxed">
            {msg.bodyText}
          </pre>
        ) : (
          <div className="text-gray-400 text-sm text-center py-8">
            No message content available.
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Search results
// ---------------------------------------------------------------------------

function SearchResults({
  accountId,
  query,
  onSelect,
  selectedUid,
}: {
  accountId: string
  query: string
  onSelect: (msg: MessageSummary) => void
  selectedUid: number | undefined
}) {
  const { data: results, isLoading, error } = useSearchMessages(accountId, query, 50)

  if (isLoading) {
    return <div className="text-center py-8 text-gray-400 text-sm">Searching...</div>
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 m-4">
        <p className="text-sm text-red-700">{error.message}</p>
      </div>
    )
  }

  if (!results || results.length === 0) {
    return (
      <div className="text-center py-12 text-gray-400 text-sm">
        No messages found matching &quot;{query}&quot;
      </div>
    )
  }

  return (
    <div>
      <div className="px-4 py-2 text-xs text-gray-500 border-b border-gray-100">
        {results.length} result{results.length !== 1 ? 's' : ''} for &quot;{query}&quot;
      </div>
      {results.map((msg) => (
        <MessageRow
          key={`${msg.id}-${msg.uid}`}
          msg={msg}
          isSelected={msg.uid === selectedUid}
          onClick={() => onSelect(msg)}
        />
      ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Main Messages page
// ---------------------------------------------------------------------------

export default function Messages() {
  const { data: accounts, isLoading: accountsLoading } = useAccounts()

  const [selectedAccountId, setSelectedAccountId] = useState<string | undefined>(undefined)
  const [selectedFolderId, setSelectedFolderId] = useState<number | undefined>(undefined)
  const [selectedMsg, setSelectedMsg] = useState<{ uid: number; folderId: number; folderPath: string } | undefined>(undefined)
  const [searchInput, setSearchInput] = useState('')
  const [searchQuery, setSearchQuery] = useState('')
  const [page, setPage] = useState(0)
  const pageSize = 50

  // Auto-select first account if none selected
  const accountId = useMemo(() => {
    if (selectedAccountId) return selectedAccountId
    if (accounts && accounts.length > 0) return accounts[0].id as string
    return undefined
  }, [selectedAccountId, accounts])

  const { data: folders, isLoading: foldersLoading } = useFolders(accountId)

  // Auto-select INBOX or first folder
  const effectiveFolderId = useMemo(() => {
    if (selectedFolderId != null) return selectedFolderId
    if (!folders || folders.length === 0) return undefined
    const inbox = folders.find(f => f.role?.toLowerCase() === 'inbox' || f.path.toLowerCase() === 'inbox')
    return inbox?.id ?? folders[0].id
  }, [selectedFolderId, folders])

  const { data: messagesData, isLoading: messagesLoading } = useMessages(accountId, effectiveFolderId, pageSize, page * pageSize)
  const messages = messagesData?.messages
  const totalCount = messagesData?.totalCount ?? 0

  const clearFolderCache = useClearFolderCache()
  const isSearching = searchQuery.length > 0

  // Keyboard navigation: arrow up/down moves between messages
  const handleKeyDown = useCallback((e: KeyboardEvent) => {
    if (!messages || messages.length === 0 || isSearching) return
    if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return

    e.preventDefault()
    const currentIdx = selectedMsg
      ? messages.findIndex(m => m.uid === selectedMsg.uid)
      : -1

    let nextIdx: number
    if (e.key === 'ArrowDown') {
      nextIdx = currentIdx < messages.length - 1 ? currentIdx + 1 : currentIdx
    } else {
      nextIdx = currentIdx > 0 ? currentIdx - 1 : 0
    }

    const nextMsg = messages[nextIdx]
    if (nextMsg) {
      const fId = nextMsg.folderId ?? effectiveFolderId!
      const fPath = nextMsg.folderPath || folders?.find(f => f.id === fId)?.path || ''
      setSelectedMsg({ uid: nextMsg.uid, folderId: fId, folderPath: fPath })
    }
  }, [messages, selectedMsg, effectiveFolderId, isSearching, folders])

  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [handleKeyDown])

  const handleAccountChange = (id: string) => {
    setSelectedAccountId(id)
    setSelectedFolderId(undefined)
    setSelectedMsg(undefined)
    setSearchQuery('')
    setSearchInput('')
    setPage(0)
  }

  const handleFolderSelect = (id: number) => {
    setSelectedFolderId(id)
    setSelectedMsg(undefined)
    setSearchQuery('')
    setSearchInput('')
    setPage(0)
  }

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault()
    setSearchQuery(searchInput.trim())
    setSelectedMsg(undefined)
    if (searchInput.trim()) {
      setSelectedFolderId(undefined) // Clear folder selection for cross-folder search
    }
  }

  const handleClearSearch = () => {
    setSearchQuery('')
    setSearchInput('')
    setSelectedMsg(undefined)
  }

  const handleSelectMessage = (msg: MessageSummary) => {
    const fId = msg.folderId ?? effectiveFolderId!
    const fPath = msg.folderPath || folders?.find(f => f.id === fId)?.path || ''
    setSelectedMsg({ uid: msg.uid, folderId: fId, folderPath: fPath })
  }

  if (accountsLoading) {
    return <div className="text-center py-8 text-gray-400">Loading accounts...</div>
  }

  if (!accounts || accounts.length === 0) {
    return (
      <div className="text-center py-12 bg-white rounded-lg shadow">
        <p className="text-gray-500">No accounts configured. Add an account first to view messages.</p>
      </div>
    )
  }

  return (
    <div className="flex flex-col h-[calc(100vh-3rem)]">
      {/* Top bar: account selector + search */}
      <div className="flex items-center gap-3 mb-4 flex-shrink-0">
        <select
          value={accountId ?? ''}
          onChange={e => handleAccountChange(e.target.value)}
          className="border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 bg-white"
        >
          {accounts.map((a) => (
            <option key={a.id as string} value={a.id as string}>
              {a.name as string}
            </option>
          ))}
        </select>

        <form onSubmit={handleSearch} className="flex-1 flex items-center gap-2">
          <input
            type="text"
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search emails — from:user@example.com subject:&quot;meeting&quot; label:urgent has:attachments before:2026-03-01"
            className="flex-1 border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <button
            type="submit"
            disabled={!searchInput.trim()}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            Search
          </button>
          {isSearching && (
            <button
              type="button"
              onClick={handleClearSearch}
              className="px-3 py-2 bg-gray-100 text-gray-600 rounded-lg text-sm hover:bg-gray-200 transition-colors"
            >
              Clear
            </button>
          )}
        </form>
      </div>

      {/* Main content: folder sidebar + message list + message detail */}
      <div className="flex flex-1 bg-white rounded-lg shadow overflow-hidden min-h-0">
        {/* Folder sidebar */}
        <div className="w-56 border-r border-gray-200 overflow-y-auto flex-shrink-0 p-2">
          {foldersLoading ? (
            <div className="text-center py-4 text-gray-400 text-xs">Loading folders...</div>
          ) : folders && folders.length > 0 ? (
            <FolderList
              folders={folders}
              selectedFolderId={isSearching ? undefined : effectiveFolderId}
              onSelect={handleFolderSelect}
            />
          ) : (
            <div className="text-center py-4 text-gray-400 text-xs">
              No folders synced yet
            </div>
          )}
        </div>

        {/* Message list */}
        <div className={`border-r border-gray-200 overflow-y-auto flex-shrink-0 ${
          selectedMsg ? 'w-80' : 'flex-1'
        }`}>
          {isSearching ? (
            <SearchResults
              accountId={accountId!}
              query={searchQuery}
              onSelect={handleSelectMessage}
              selectedUid={selectedMsg?.uid}
            />
          ) : effectiveFolderId == null ? (
            <div className="text-center py-12 text-gray-400 text-sm">
              Select a folder to view messages
            </div>
          ) : messagesLoading ? (
            <div className="text-center py-8 text-gray-400 text-sm">Loading messages...</div>
          ) : messages && messages.length > 0 ? (
            <>
              <div className="px-4 py-2 text-xs text-gray-500 border-b border-gray-100 bg-gray-50 flex items-center justify-between">
                <span>
                  {totalCount.toLocaleString()} message{totalCount !== 1 ? 's' : ''}
                  {folders && (
                    <> in {folders.find(f => f.id === effectiveFolderId)?.displayName ?? 'folder'}</>
                  )}
                </span>
                {accountId && effectiveFolderId != null && (
                  <button
                    onClick={() => {
                      if (window.confirm('Clear cached messages for this folder?'))
                        clearFolderCache.mutate({ accountId, folderId: effectiveFolderId })
                    }}
                    disabled={clearFolderCache.isPending}
                    className="px-2 py-0.5 text-xs text-red-600 bg-red-50 border border-red-200 rounded hover:bg-red-100 transition-colors disabled:opacity-50"
                    title="Clear folder cache"
                  >
                    {clearFolderCache.isPending ? 'Clearing...' : 'Clear Cache'}
                  </button>
                )}
                {clearFolderCache.error && (
                  <span className="ml-2 text-xs text-red-600" title={clearFolderCache.error.message}>
                    Clear failed
                  </span>
                )}
              </div>
              {messages.map((msg) => (
                <MessageRow
                  key={`${msg.id}-${msg.uid}`}
                  msg={msg}
                  isSelected={selectedMsg?.uid === msg.uid}
                  onClick={() => handleSelectMessage(msg)}
                />
              ))}
              <div className="flex items-center justify-between px-4 py-2 border-t border-gray-100 bg-gray-50">
                <button
                  onClick={() => setPage(p => Math.max(0, p - 1))}
                  disabled={page === 0}
                  className="px-3 py-1 text-xs text-gray-600 bg-white border border-gray-200 rounded hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  &larr; Previous
                </button>
                <span className="text-xs text-gray-500">
                  Page {page + 1} of {Math.max(1, Math.ceil(totalCount / pageSize))}
                </span>
                <button
                  onClick={() => setPage(p => p + 1)}
                  disabled={(page + 1) * pageSize >= totalCount}
                  className="px-3 py-1 text-xs text-gray-600 bg-white border border-gray-200 rounded hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  Next &rarr;
                </button>
              </div>
            </>
          ) : (
            <div className="text-center py-12 text-gray-400 text-sm">
              No messages in this folder
            </div>
          )}
        </div>

        {/* Message detail pane */}
        {selectedMsg && accountId && (
          <div className="flex-1 overflow-hidden min-w-0">
            <MessageView
              accountId={accountId}
              folderId={selectedMsg.folderId}
              folderPath={selectedMsg.folderPath}
              uid={selectedMsg.uid}
              onClose={() => setSelectedMsg(undefined)}
            />
          </div>
        )}
      </div>
    </div>
  )
}
