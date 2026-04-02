import React, { useState, useMemo } from 'react'
import { useAccounts, useExecuteTool } from '../hooks/useApi'

interface DuplicateCopy {
  account_id: string
  db_id: number
  folder_path: string | null
  uid: number
  date: string | null
}

interface DuplicateGroup {
  message_id: string
  subject: string | null
  from: string | null
  date: string | null
  copies: DuplicateCopy[]
}

interface DetectResult {
  duplicate_groups: number
  total_duplicates: number
  groups: DuplicateGroup[]
}

export default function Duplicates() {
  const { data: accounts } = useAccounts()
  const executeTool = useExecuteTool()

  // Scan state
  const [filterAccountId, setFilterAccountId] = useState('')
  const [result, setResult] = useState<DetectResult | null>(null)
  const [scanning, setScanning] = useState(false)
  const [scanError, setScanError] = useState<string | null>(null)

  // Expand / select state
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set())
  const [selectedCopies, setSelectedCopies] = useState<Set<string>>(new Set())

  // Bulk delete state
  const [bulkDeleteAccount, setBulkDeleteAccount] = useState('')

  // Action feedback
  const [actionStatus, setActionStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null)
  const [deleting, setDeleting] = useState(false)

  const accountMap = useMemo(() => {
    const map: Record<string, string> = {}
    if (accounts) {
      for (const a of accounts) {
        map[a.id as string] = a.name as string
      }
    }
    return map
  }, [accounts])

  // --- Handlers ---

  const handleScan = async () => {
    setScanning(true)
    setScanError(null)
    setResult(null)
    setExpandedIds(new Set())
    setSelectedCopies(new Set())
    setActionStatus(null)

    try {
      const args: Record<string, unknown> = { limit: 100 }
      if (filterAccountId) args.accountId = filterAccountId
      const res = await executeTool.mutateAsync({ name: 'detect_duplicates', args })
      const parsed = parseDetectResult(res)
      setResult(parsed)
    } catch (err) {
      setScanError((err as Error).message)
    } finally {
      setScanning(false)
    }
  }

  const toggleExpand = (messageId: string) => {
    setExpandedIds(prev => {
      const next = new Set(prev)
      if (next.has(messageId)) next.delete(messageId)
      else next.add(messageId)
      return next
    })
  }

  const toggleSelectCopy = (copyKey: string) => {
    setSelectedCopies(prev => {
      const next = new Set(prev)
      if (next.has(copyKey)) next.delete(copyKey)
      else next.add(copyKey)
      return next
    })
  }

  const getSelectedCopyObjects = (): DuplicateCopy[] => {
    if (!result) return []
    const selected: DuplicateCopy[] = []
    for (const group of result.groups) {
      for (const copy of group.copies) {
        if (selectedCopies.has(String(copy.db_id))) {
          selected.push(copy)
        }
      }
    }
    return selected
  }

  const handleDeleteSelected = async () => {
    const copies = getSelectedCopyObjects()
    if (copies.length === 0) return
    if (!window.confirm(`Delete ${copies.length} selected copy/copies? Messages will be moved to trash on most IMAP servers.`)) return

    setDeleting(true)
    setActionStatus(null)

    try {
      // Group by account + folder
      const grouped: Record<string, DuplicateCopy[]> = {}
      for (const copy of copies) {
        const folder = copy.folder_path || 'INBOX'
        const key = `${copy.account_id}:${folder}`
        if (!grouped[key]) grouped[key] = []
        grouped[key].push(copy)
      }

      for (const [key, groupCopies] of Object.entries(grouped)) {
        const separatorIdx = key.indexOf(':')
        const accountId = key.slice(0, separatorIdx)
        const folder = key.slice(separatorIdx + 1)
        const uids = groupCopies.map(c => c.uid).join(',')
        await executeTool.mutateAsync({
          name: 'delete_messages',
          args: { accountId, uids, folder },
        })
      }

      setActionStatus({ type: 'success', message: `Queued deletion of ${copies.length} message(s).` })
      setSelectedCopies(new Set())
    } catch (err) {
      setActionStatus({ type: 'error', message: (err as Error).message })
    } finally {
      setDeleting(false)
    }
  }

  const handleBulkDelete = async () => {
    if (!bulkDeleteAccount || !result) return

    // Collect all copies from the selected account that have copies in other accounts
    const copies: DuplicateCopy[] = []
    for (const group of result.groups) {
      const hasOtherAccount = group.copies.some(c => c.account_id !== bulkDeleteAccount)
      if (!hasOtherAccount) continue
      for (const copy of group.copies) {
        if (copy.account_id === bulkDeleteAccount) {
          copies.push(copy)
        }
      }
    }

    if (copies.length === 0) {
      setActionStatus({ type: 'error', message: 'No duplicate copies found for this account.' })
      return
    }

    const accountName = accountMap[bulkDeleteAccount] || bulkDeleteAccount
    if (!window.confirm(`Delete ${copies.length} duplicate(s) from "${accountName}" that exist in other accounts? Messages will be moved to trash on most IMAP servers.`)) return

    setDeleting(true)
    setActionStatus(null)

    try {
      const grouped: Record<string, DuplicateCopy[]> = {}
      for (const copy of copies) {
        const folder = copy.folder_path || 'INBOX'
        const key = `${copy.account_id}:${folder}`
        if (!grouped[key]) grouped[key] = []
        grouped[key].push(copy)
      }

      for (const [key, groupCopies] of Object.entries(grouped)) {
        const separatorIdx = key.indexOf(':')
        const accountId = key.slice(0, separatorIdx)
        const folder = key.slice(separatorIdx + 1)
        const uids = groupCopies.map(c => c.uid).join(',')
        await executeTool.mutateAsync({
          name: 'delete_messages',
          args: { accountId, uids, folder },
        })
      }

      setActionStatus({ type: 'success', message: `Queued deletion of ${copies.length} duplicate(s) from "${accountName}".` })
      setSelectedCopies(new Set())
    } catch (err) {
      setActionStatus({ type: 'error', message: (err as Error).message })
    } finally {
      setDeleting(false)
    }
  }

  // Unique accounts across all copies in the result (for bulk delete dropdown)
  const accountsInResults = useMemo(() => {
    if (!result) return []
    const ids = new Set<string>()
    for (const group of result.groups) {
      for (const copy of group.copies) {
        ids.add(copy.account_id)
      }
    }
    return Array.from(ids)
  }, [result])

  return (
    <div>
      <div className="mb-6">
        <h2 className="text-2xl font-semibold text-gray-900">Duplicate Emails</h2>
        <p className="mt-1 text-sm text-gray-500">
          Emails that exist in multiple accounts (matched by Message-ID header)
        </p>
      </div>

      {/* Scan controls */}
      <div className="bg-white rounded-lg shadow p-4 mb-6">
        <div className="flex flex-wrap items-end gap-4">
          <div className="flex-1 min-w-48">
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Filter by account (optional)
            </label>
            <select
              value={filterAccountId}
              onChange={(e) => setFilterAccountId(e.target.value)}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="">All accounts</option>
              {accounts?.map((a) => (
                <option key={a.id as string} value={a.id as string}>
                  {a.name as string}
                </option>
              ))}
            </select>
          </div>
          <button
            onClick={handleScan}
            disabled={scanning}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm font-medium hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {scanning ? 'Scanning...' : 'Scan for Duplicates'}
          </button>
        </div>
      </div>

      {/* Scan error */}
      {scanError && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-700">{scanError}</p>
        </div>
      )}

      {/* Loading state */}
      {scanning && (
        <div className="text-center py-8 text-gray-500">Scanning for duplicate emails...</div>
      )}

      {/* Empty state */}
      {result !== null && result.groups.length === 0 && !scanning && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No duplicate emails found across accounts.</p>
        </div>
      )}

      {/* Action feedback */}
      {actionStatus && (
        <div className={`rounded-lg p-4 mb-4 border ${
          actionStatus.type === 'success'
            ? 'bg-green-50 border-green-200'
            : 'bg-red-50 border-red-200'
        }`}>
          <p className={`text-sm ${actionStatus.type === 'success' ? 'text-green-700' : 'text-red-700'}`}>
            {actionStatus.message}
          </p>
        </div>
      )}

      {/* Results table */}
      {result !== null && result.groups.length > 0 && (
        <>
          <div className="bg-white rounded-lg shadow overflow-hidden mb-6">
            <div className="px-4 py-3 border-b border-gray-200 bg-gray-50 flex items-center justify-between">
              <span className="text-sm font-medium text-gray-700">
                {result.duplicate_groups} duplicate group{result.duplicate_groups !== 1 ? 's' : ''} ({result.total_duplicates} total copies)
              </span>
              {selectedCopies.size > 0 && (
                <span className="text-sm text-blue-600 font-medium">
                  {selectedCopies.size} selected
                </span>
              )}
            </div>
            <table className="w-full text-sm">
              <thead className="bg-gray-50 border-b">
                <tr>
                  <th className="text-left px-4 py-3 font-medium text-gray-500 w-8"></th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Subject</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">From</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Date</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Accounts</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500 w-16">Copies</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {result.groups.map(group => (
                  <React.Fragment key={group.message_id}>
                    <tr
                      className="hover:bg-gray-50 cursor-pointer"
                      onClick={() => toggleExpand(group.message_id)}
                    >
                      <td className="px-4 py-3 text-gray-400">
                        <svg
                          className={`w-4 h-4 transition-transform ${expandedIds.has(group.message_id) ? 'rotate-90' : ''}`}
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                        </svg>
                      </td>
                      <td className="px-4 py-3 text-gray-900 max-w-xs truncate" title={group.subject || ''}>
                        {group.subject || '(no subject)'}
                      </td>
                      <td className="px-4 py-3 text-gray-600 max-w-xs truncate" title={group.from || ''}>
                        {group.from || ''}
                      </td>
                      <td className="px-4 py-3 text-gray-500 text-xs whitespace-nowrap">
                        {group.date || ''}
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap gap-1">
                          {getUniqueAccounts(group.copies).map(aid => (
                            <span
                              key={aid}
                              className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700"
                            >
                              {accountMap[aid] || aid}
                            </span>
                          ))}
                        </div>
                      </td>
                      <td className="px-4 py-3 text-gray-500 text-center">
                        {group.copies.length}
                      </td>
                    </tr>
                    {expandedIds.has(group.message_id) && (
                      <tr key={`${group.message_id}-detail`}>
                        <td colSpan={6} className="px-4 py-3 bg-gray-50">
                          <div className="ml-8 space-y-0">
                            {group.copies.map(copy => (
                              <div
                                key={copy.db_id}
                                className="flex items-center gap-3 py-1.5 border-t border-gray-200 first:border-t-0"
                              >
                                <input
                                  type="checkbox"
                                  checked={selectedCopies.has(String(copy.db_id))}
                                  onChange={(e) => {
                                    e.stopPropagation()
                                    toggleSelectCopy(String(copy.db_id))
                                  }}
                                  onClick={(e) => e.stopPropagation()}
                                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                />
                                <span className="text-sm text-gray-700 min-w-24">
                                  {accountMap[copy.account_id] || copy.account_id}
                                </span>
                                <span className="font-mono text-xs text-gray-600">
                                  {copy.folder_path || '(unknown)'}
                                </span>
                                <span className="text-xs text-gray-500">
                                  UID: {copy.uid}
                                </span>
                                {copy.date && (
                                  <span className="text-xs text-gray-400">
                                    {copy.date}
                                  </span>
                                )}
                              </div>
                            ))}
                          </div>
                        </td>
                      </tr>
                    )}
                  </React.Fragment>
                ))}
              </tbody>
            </table>
          </div>

          {/* Action bar */}
          <div className="bg-white rounded-lg shadow p-4">
            <h3 className="text-lg font-medium text-gray-900 mb-3">Delete Duplicates</h3>

            {/* Selected delete */}
            <div className="flex flex-wrap items-center gap-4 mb-4 pb-4 border-b border-gray-200">
              <p className="text-sm text-gray-600">
                {selectedCopies.size > 0
                  ? `${selectedCopies.size} copy/copies selected`
                  : 'Expand groups above and select individual copies to delete'}
              </p>
              <button
                onClick={handleDeleteSelected}
                disabled={selectedCopies.size === 0 || deleting}
                className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {deleting ? 'Queuing...' : 'Delete Selected'}
              </button>
            </div>

            {/* Bulk delete */}
            <div>
              <p className="text-sm text-gray-500 mb-3">
                Or bulk-delete all copies from one account that also exist in other accounts.
              </p>
              <div className="flex flex-wrap items-end gap-4">
                <div className="flex-1 min-w-48">
                  <label className="block text-sm font-medium text-gray-700 mb-1">
                    Delete all copies from
                  </label>
                  <select
                    value={bulkDeleteAccount}
                    onChange={(e) => setBulkDeleteAccount(e.target.value)}
                    className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                  >
                    <option value="">Select an account...</option>
                    {accountsInResults.map(aid => (
                      <option key={aid} value={aid}>
                        {accountMap[aid] || aid}
                      </option>
                    ))}
                  </select>
                </div>
                <button
                  onClick={handleBulkDelete}
                  disabled={!bulkDeleteAccount || deleting}
                  className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {deleting ? 'Queuing...' : 'Delete All From Account'}
                </button>
              </div>
            </div>
          </div>
        </>
      )}
    </div>
  )
}

// --- Helpers ---

function getUniqueAccounts(copies: DuplicateCopy[]): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const c of copies) {
    if (!seen.has(c.account_id)) {
      seen.add(c.account_id)
      result.push(c.account_id)
    }
  }
  return result
}

/**
 * Parse the tool execute response into a DetectResult.
 * The dashboard API parses the tool's JSON string and returns it directly,
 * so the response is the parsed object. We handle multiple response shapes
 * for robustness.
 */
function parseDetectResult(res: unknown): DetectResult {
  const empty: DetectResult = { duplicate_groups: 0, total_duplicates: 0, groups: [] }
  if (!res) return empty

  // If it's a string, try parsing it
  if (typeof res === 'string') {
    try {
      return parseDetectResult(JSON.parse(res))
    } catch {
      return empty
    }
  }

  const obj = res as Record<string, unknown>

  // Direct shape: { duplicate_groups, total_duplicates, groups }
  if (obj.groups && Array.isArray(obj.groups)) {
    return {
      duplicate_groups: (obj.duplicate_groups as number) || (obj.groups as unknown[]).length,
      total_duplicates: (obj.total_duplicates as number) || 0,
      groups: obj.groups as DuplicateGroup[],
    }
  }

  // MCP content format: { content: [{ type: "text", text: "..." }] }
  if (obj.content && Array.isArray(obj.content)) {
    for (const item of obj.content as Array<Record<string, unknown>>) {
      if (item.type === 'text' && typeof item.text === 'string') {
        try {
          return parseDetectResult(JSON.parse(item.text))
        } catch {
          // skip
        }
      }
    }
  }

  return empty
}
