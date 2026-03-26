import { useState, useMemo } from 'react'
import {
  useAccounts,
  useFolders,
  useMessages,
  useMessage,
  useSearchMessages,
  useFetchBody,
  useClearFolderCache,
  type MessageSummary,
  type FolderInfo,
} from '../hooks/useApi'

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
            {msg.subject}
          </div>
          {msg.snippet && (
            <div className="text-xs text-gray-400 truncate mt-0.5">{msg.snippet}</div>
          )}
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
  uid,
  onClose,
}: {
  accountId: string
  folderId: number
  uid: number
  onClose: () => void
}) {
  const { data: msg, isLoading, error } = useMessage(accountId, folderId, uid)
  const fetchBody = useFetchBody()
  const [showHtml, setShowHtml] = useState(false)

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
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600 p-1 flex-shrink-0"
            title="Close"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

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
          <iframe
            srcDoc={msg.bodyHtml!}
            title="Email body"
            className="w-full h-full border-0 min-h-[400px] rounded bg-white"
            sandbox="allow-same-origin"
          />
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
  const [selectedMsg, setSelectedMsg] = useState<{ uid: number; folderId: number } | undefined>(undefined)
  const [searchInput, setSearchInput] = useState('')
  const [searchQuery, setSearchQuery] = useState('')

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

  const { data: messages, isLoading: messagesLoading } = useMessages(accountId, effectiveFolderId, 100)

  const clearFolderCache = useClearFolderCache()
  const isSearching = searchQuery.length > 0

  const handleAccountChange = (id: string) => {
    setSelectedAccountId(id)
    setSelectedFolderId(undefined)
    setSelectedMsg(undefined)
    setSearchQuery('')
    setSearchInput('')
  }

  const handleFolderSelect = (id: number) => {
    setSelectedFolderId(id)
    setSelectedMsg(undefined)
    setSearchQuery('')
    setSearchInput('')
  }

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault()
    setSearchQuery(searchInput.trim())
    setSelectedMsg(undefined)
  }

  const handleClearSearch = () => {
    setSearchQuery('')
    setSearchInput('')
    setSelectedMsg(undefined)
  }

  const handleSelectMessage = (msg: MessageSummary) => {
    setSelectedMsg({ uid: msg.uid, folderId: msg.folderId ?? effectiveFolderId! })
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
            placeholder="Search messages..."
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
              selectedFolderId={effectiveFolderId}
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
                  {messages.length} message{messages.length !== 1 ? 's' : ''}
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
              uid={selectedMsg.uid}
              onClose={() => setSelectedMsg(undefined)}
            />
          </div>
        )}
      </div>
    </div>
  )
}
