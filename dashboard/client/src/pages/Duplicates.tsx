import { useState, useMemo, useCallback } from 'react'
import { useAccounts, useExecuteTool } from '../hooks/useApi'

interface DuplicateCopy {
  accountId: string
  accountName: string
  folder: string
  uid: number
  date: string
}

interface DuplicateGroup {
  messageId: string
  subject: string
  from: string
  date: string
  copies: DuplicateCopy[]
}

export default function Duplicates() {
  const { data: accounts } = useAccounts()
  const executeTool = useExecuteTool()

  const [filterAccountId, setFilterAccountId] = useState<string>('')
  const [duplicates, setDuplicates] = useState<DuplicateGroup[] | null>(null)
  const [scanning, setScanning] = useState(false)
  const [scanError, setScanError] = useState<string | null>(null)
  const [expandedMessageId, setExpandedMessageId] = useState<string | null>(null)

  // Bulk delete state
  const [deleteAccountId, setDeleteAccountId] = useState<string>('')
  const [dryRunResult, setDryRunResult] = useState<{ count: number; accountId: string } | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteResult, setDeleteResult] = useState<string | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const accountNameMap = useMemo(() => {
    const map: Record<string, string> = {}
    if (accounts) {
      for (const a of accounts) {
        map[a.id as string] = a.name as string
      }
    }
    return map
  }, [accounts])

  const handleScan = useCallback(async () => {
    setScanning(true)
    setScanError(null)
    setDuplicates(null)
    setDryRunResult(null)
    setDeleteResult(null)
    setDeleteError(null)

    try {
      const args: Record<string, unknown> = { limit: 100 }
      if (filterAccountId) args.accountId = filterAccountId
      const res = await executeTool.mutateAsync({ name: 'detect_duplicates', args })

      // The tool returns MCP content — parse the text result
      const parsed = parseDuplicatesResult(res)
      setDuplicates(parsed)
    } catch (err) {
      setScanError((err as Error).message)
    } finally {
      setScanning(false)
    }
  }, [executeTool, filterAccountId])

  const handleDryRun = useCallback(async () => {
    if (!deleteAccountId) return
    setDeleting(true)
    setDeleteError(null)
    setDeleteResult(null)
    setDryRunResult(null)

    try {
      const res = await executeTool.mutateAsync({
        name: 'delete_duplicates',
        args: { accountId: deleteAccountId, dryRun: true },
      })
      const count = parseDryRunCount(res)
      setDryRunResult({ count, accountId: deleteAccountId })
    } catch (err) {
      setDeleteError((err as Error).message)
    } finally {
      setDeleting(false)
    }
  }, [executeTool, deleteAccountId])

  const handleDeleteConfirm = useCallback(async () => {
    if (!dryRunResult) return
    setDeleting(true)
    setDeleteError(null)

    try {
      const res = await executeTool.mutateAsync({
        name: 'delete_duplicates',
        args: { accountId: dryRunResult.accountId, dryRun: false },
      })
      const count = parseDeleteCount(res)
      setDeleteResult(`Successfully deleted ${count} duplicate email(s) from ${accountNameMap[dryRunResult.accountId] || dryRunResult.accountId}.`)
      setDryRunResult(null)
      // Re-scan to refresh the list
      setDuplicates(null)
    } catch (err) {
      setDeleteError((err as Error).message)
    } finally {
      setDeleting(false)
    }
  }, [executeTool, dryRunResult, accountNameMap])

  const toggleExpand = (messageId: string) => {
    setExpandedMessageId(expandedMessageId === messageId ? null : messageId)
  }

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
      {duplicates !== null && duplicates.length === 0 && !scanning && (
        <div className="text-center py-12 bg-white rounded-lg shadow">
          <p className="text-gray-500">No duplicate emails found across accounts.</p>
        </div>
      )}

      {/* Results table */}
      {duplicates !== null && duplicates.length > 0 && (
        <>
          <div className="bg-white rounded-lg shadow overflow-hidden mb-6">
            <div className="px-4 py-3 border-b border-gray-200 bg-gray-50">
              <span className="text-sm font-medium text-gray-700">
                {duplicates.length} duplicate group{duplicates.length !== 1 ? 's' : ''} found
              </span>
            </div>
            <table className="w-full text-sm">
              <thead className="bg-gray-50 border-b">
                <tr>
                  <th className="text-left px-4 py-3 font-medium text-gray-500 w-8"></th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Subject</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">From</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Date</th>
                  <th className="text-left px-4 py-3 font-medium text-gray-500">Accounts</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {duplicates.map((group) => (
                  <>
                    <tr
                      key={group.messageId}
                      className="hover:bg-gray-50 cursor-pointer"
                      onClick={() => toggleExpand(group.messageId)}
                    >
                      <td className="px-4 py-3 text-gray-400">
                        <svg
                          className={`w-4 h-4 transition-transform ${expandedMessageId === group.messageId ? 'rotate-90' : ''}`}
                          fill="none"
                          stroke="currentColor"
                          viewBox="0 0 24 24"
                        >
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                        </svg>
                      </td>
                      <td className="px-4 py-3 text-gray-900 max-w-xs truncate" title={group.subject}>
                        {group.subject || '(no subject)'}
                      </td>
                      <td className="px-4 py-3 text-gray-600 max-w-xs truncate" title={group.from}>
                        {group.from}
                      </td>
                      <td className="px-4 py-3 text-gray-500 text-xs whitespace-nowrap">
                        {group.date}
                      </td>
                      <td className="px-4 py-3">
                        <div className="flex flex-wrap gap-1">
                          {getUniqueAccounts(group.copies).map((accountId) => (
                            <span
                              key={accountId}
                              className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700"
                            >
                              {accountNameMap[accountId] || accountId}
                            </span>
                          ))}
                        </div>
                      </td>
                    </tr>
                    {expandedMessageId === group.messageId && (
                      <tr key={`${group.messageId}-expanded`}>
                        <td colSpan={5} className="px-4 py-3 bg-gray-50">
                          <div className="ml-8">
                            <table className="w-full text-xs">
                              <thead>
                                <tr>
                                  <th className="text-left py-1 pr-4 font-medium text-gray-500">Account</th>
                                  <th className="text-left py-1 pr-4 font-medium text-gray-500">Folder</th>
                                  <th className="text-left py-1 pr-4 font-medium text-gray-500">UID</th>
                                  <th className="text-left py-1 pr-4 font-medium text-gray-500">Date</th>
                                </tr>
                              </thead>
                              <tbody>
                                {group.copies.map((copy, idx) => (
                                  <tr key={idx} className="border-t border-gray-200">
                                    <td className="py-1.5 pr-4 text-gray-700">
                                      {copy.accountName || accountNameMap[copy.accountId] || copy.accountId}
                                    </td>
                                    <td className="py-1.5 pr-4 text-gray-600 font-mono">{copy.folder}</td>
                                    <td className="py-1.5 pr-4 text-gray-500 font-mono">{copy.uid}</td>
                                    <td className="py-1.5 pr-4 text-gray-500">{copy.date}</td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          </div>
                        </td>
                      </tr>
                    )}
                  </>
                ))}
              </tbody>
            </table>
          </div>

          {/* Bulk delete section */}
          <div className="bg-white rounded-lg shadow p-4">
            <h3 className="text-lg font-medium text-gray-900 mb-3">Bulk Delete Duplicates</h3>
            <p className="text-sm text-gray-500 mb-4">
              Select an account to remove all duplicate copies from. Only emails that exist in other accounts will be deleted.
            </p>

            <div className="flex flex-wrap items-end gap-4">
              <div className="flex-1 min-w-48">
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Delete duplicates from account
                </label>
                <select
                  value={deleteAccountId}
                  onChange={(e) => {
                    setDeleteAccountId(e.target.value)
                    setDryRunResult(null)
                    setDeleteResult(null)
                    setDeleteError(null)
                  }}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value="">Select an account...</option>
                  {accounts?.map((a) => (
                    <option key={a.id as string} value={a.id as string}>
                      {a.name as string}
                    </option>
                  ))}
                </select>
              </div>
              <button
                onClick={handleDryRun}
                disabled={!deleteAccountId || deleting}
                className="px-4 py-2 bg-yellow-500 text-white rounded-lg text-sm font-medium hover:bg-yellow-600 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {deleting && !dryRunResult ? 'Checking...' : 'Preview Deletion'}
              </button>
            </div>

            {/* Dry run result — confirm step */}
            {dryRunResult && (
              <div className="mt-4 p-4 bg-yellow-50 border border-yellow-200 rounded-lg">
                <p className="text-sm text-yellow-800 mb-3">
                  <strong>{dryRunResult.count}</strong> duplicate email{dryRunResult.count !== 1 ? 's' : ''} will be deleted
                  from <strong>{accountNameMap[dryRunResult.accountId] || dryRunResult.accountId}</strong>.
                  This action cannot be undone.
                </p>
                <div className="flex gap-2">
                  <button
                    onClick={handleDeleteConfirm}
                    disabled={deleting}
                    className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {deleting ? 'Deleting...' : 'Confirm Delete'}
                  </button>
                  <button
                    onClick={() => setDryRunResult(null)}
                    disabled={deleting}
                    className="px-4 py-2 bg-gray-200 text-gray-700 rounded-lg text-sm font-medium hover:bg-gray-300 transition-colors disabled:opacity-50"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}

            {/* Delete success */}
            {deleteResult && (
              <div className="mt-4 p-4 bg-green-50 border border-green-200 rounded-lg">
                <p className="text-sm text-green-700">{deleteResult}</p>
              </div>
            )}

            {/* Delete error */}
            {deleteError && (
              <div className="mt-4 p-4 bg-red-50 border border-red-200 rounded-lg">
                <p className="text-sm text-red-700">{deleteError}</p>
              </div>
            )}
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
    if (!seen.has(c.accountId)) {
      seen.add(c.accountId)
      result.push(c.accountId)
    }
  }
  return result
}

/**
 * Parse the tool result into structured duplicate groups.
 * The MCP tool returns content as text/JSON — we handle both structured and text formats.
 */
function parseDuplicatesResult(res: unknown): DuplicateGroup[] {
  if (!res) return []

  // If it's directly an array of duplicate groups
  if (Array.isArray(res)) {
    return res as DuplicateGroup[]
  }

  // If it has a 'content' array (MCP tool response format)
  const obj = res as Record<string, unknown>
  if (obj.content && Array.isArray(obj.content)) {
    for (const item of obj.content as Array<Record<string, unknown>>) {
      if (item.type === 'text' && typeof item.text === 'string') {
        try {
          const parsed = JSON.parse(item.text)
          if (Array.isArray(parsed)) return parsed as DuplicateGroup[]
          if (parsed.duplicates && Array.isArray(parsed.duplicates)) return parsed.duplicates as DuplicateGroup[]
          if (parsed.groups && Array.isArray(parsed.groups)) return parsed.groups as DuplicateGroup[]
        } catch {
          // Not JSON text, skip
        }
      }
    }
  }

  // If it has a duplicates/groups property directly
  if (obj.duplicates && Array.isArray(obj.duplicates)) return obj.duplicates as DuplicateGroup[]
  if (obj.groups && Array.isArray(obj.groups)) return obj.groups as DuplicateGroup[]

  // Try parsing the whole thing as text
  if (typeof res === 'string') {
    try {
      const parsed = JSON.parse(res)
      return parseDuplicatesResult(parsed)
    } catch {
      return []
    }
  }

  return []
}

function parseDryRunCount(res: unknown): number {
  if (!res) return 0
  const obj = res as Record<string, unknown>

  // Direct count property
  if (typeof obj.count === 'number') return obj.count
  if (typeof obj.duplicateCount === 'number') return obj.duplicateCount

  // MCP content format
  if (obj.content && Array.isArray(obj.content)) {
    for (const item of obj.content as Array<Record<string, unknown>>) {
      if (item.type === 'text' && typeof item.text === 'string') {
        try {
          const parsed = JSON.parse(item.text)
          if (typeof parsed.count === 'number') return parsed.count
          if (typeof parsed.duplicateCount === 'number') return parsed.duplicateCount
          if (typeof parsed === 'number') return parsed
        } catch {
          // Try to extract a number from the text
          const match = (item.text as string).match(/(\d+)\s+duplicate/i)
          if (match) return parseInt(match[1], 10)
        }
      }
    }
  }

  return 0
}

function parseDeleteCount(res: unknown): number {
  if (!res) return 0
  const obj = res as Record<string, unknown>

  if (typeof obj.deleted === 'number') return obj.deleted
  if (typeof obj.count === 'number') return obj.count
  if (typeof obj.deletedCount === 'number') return obj.deletedCount

  // MCP content format
  if (obj.content && Array.isArray(obj.content)) {
    for (const item of obj.content as Array<Record<string, unknown>>) {
      if (item.type === 'text' && typeof item.text === 'string') {
        try {
          const parsed = JSON.parse(item.text)
          if (typeof parsed.deleted === 'number') return parsed.deleted
          if (typeof parsed.count === 'number') return parsed.count
          if (typeof parsed.deletedCount === 'number') return parsed.deletedCount
        } catch {
          const match = (item.text as string).match(/(\d+)\s+(?:deleted|removed)/i)
          if (match) return parseInt(match[1], 10)
        }
      }
    }
  }

  return 0
}
