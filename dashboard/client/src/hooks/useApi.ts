import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'

async function apiFetch<T>(url: string, options?: RequestInit): Promise<T> {
  const token = localStorage.getItem('dashboard_token')
  const headers: HeadersInit = { 'Content-Type': 'application/json' }
  if (token) {
    headers['Authorization'] = `Bearer ${token}`
  }
  const res = await fetch(url, { ...options, headers: { ...headers, ...options?.headers } })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error || body?.Error || body?.message || `API error: ${res.status}`)
  }
  return res.json()
}

// ---------- Query hooks ----------

export function useAccounts() {
  return useQuery({
    queryKey: ['accounts'],
    queryFn: () => apiFetch<Array<Record<string, unknown>>>('/api/accounts'),
  })
}

export function useSyncStatus() {
  return useQuery({
    queryKey: ['sync-status'],
    queryFn: () => apiFetch<Record<string, unknown>>('/api/sync/status'),
    refetchInterval: 10000,
  })
}

export function useTriggerSync() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (params: { accountId: string; folderPath?: string }) =>
      apiFetch<{ status: string }>('/api/sync/trigger', {
        method: 'POST',
        body: JSON.stringify({ accountId: params.accountId, folderPath: params.folderPath }),
      }),
    onSuccess: () => {
      setTimeout(() => qc.invalidateQueries({ queryKey: ['sync-status'] }), 2000)
    },
  })
}

export function useQueue(status?: string) {
  const url = status ? `/api/queue?status=${status}` : '/api/queue'
  return useQuery({
    queryKey: ['queue', status],
    queryFn: () => apiFetch<Array<Record<string, unknown>>>(url),
    refetchInterval: 5000,
  })
}

export function useSettings() {
  return useQuery({
    queryKey: ['settings'],
    queryFn: () => apiFetch<Record<string, unknown>>('/api/settings'),
  })
}

export interface LogEntry {
  id: number
  level: string
  category: string
  message: string
  exception: string | null
  metadata: string | null
  created_at: string
  scope: string
  instance_id: string
}

export interface LogsResponse {
  count: number
  total_count: number
  page: number
  page_size: number
  logs: LogEntry[]
}

export function useLogs(params: { levels?: string; search?: string; page?: number; page_size?: number; scope?: string; instance_id?: string }) {
  const qs = new URLSearchParams()
  if (params.levels) qs.set('level', params.levels)
  if (params.search) qs.set('search', params.search)
  if (params.page) qs.set('page', String(params.page))
  if (params.page_size) qs.set('page_size', String(params.page_size))
  if (params.scope) qs.set('scope', params.scope)
  if (params.instance_id) qs.set('instance_id', params.instance_id)
  return useQuery({
    queryKey: ['logs', params.levels, params.search, params.page, params.page_size, params.scope, params.instance_id],
    queryFn: () => apiFetch<LogsResponse>(`/api/logs?${qs}`),
    refetchInterval: 5000,
  })
}

export function useLogInstances() {
  return useQuery({
    queryKey: ['log-instances'],
    queryFn: () => apiFetch<string[]>('/api/logs/instances'),
    refetchInterval: 30000,
  })
}

export function useAuthStatus() {
  return useQuery({
    queryKey: ['auth-status'],
    queryFn: () => apiFetch<{ hasPinSet: boolean }>('/api/auth/status'),
  })
}

// ---------- Mutation hooks ----------

export function useCreateAccount() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (data: CreateAccountRequest) =>
      apiFetch<{ id: string }>('/api/accounts', { method: 'POST', body: JSON.stringify(data) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['accounts'] }),
  })
}

export function useDeleteAccount() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<unknown>(`/api/accounts/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['accounts'] }),
  })
}

export function useTestAccount() {
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<{ success: boolean; message: string }>(`/api/accounts/${id}/test`, { method: 'POST' }),
  })
}

export interface RecentEmail {
  subject: string
  from: string
  date: string
}

export function useFetchRecent() {
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<{ success: boolean; total: number; emails: RecentEmail[]; message?: string }>(
        `/api/accounts/${id}/fetch-recent`, { method: 'POST' }),
  })
}

export function useUpdateSettings() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (data: Record<string, unknown>) =>
      apiFetch<unknown>('/api/settings', { method: 'PUT', body: JSON.stringify(data) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['settings'] }),
  })
}

export function useSetupPin() {
  return useMutation({
    mutationFn: (pin: string) =>
      apiFetch<{ token: string }>('/api/auth/setup', { method: 'POST', body: JSON.stringify({ pin }) }),
  })
}

export function useChangePin() {
  return useMutation({
    mutationFn: (data: { old_pin?: string; new_pin: string }) =>
      apiFetch<{ token: string; message: string }>('/api/auth/change-pin', {
        method: 'POST',
        body: JSON.stringify(data),
      }),
  })
}

// ---------- OAuth hooks ----------

export interface OAuthProviderInfo {
  provider: string
  configured: boolean
}

export function useOAuthProviders() {
  return useQuery({
    queryKey: ['oauth-providers'],
    queryFn: async () => {
      const list = await apiFetch<OAuthProviderInfo[]>('/api/oauth/providers')
      // Index by provider name for easy lookup
      const map: Record<string, OAuthProviderInfo> = {}
      for (const p of list) map[p.provider] = p
      return map
    },
  })
}

export function useStartOAuth() {
  return useMutation({
    mutationFn: (params: { provider: string; clientId?: string; clientSecret?: string }) => {
      const qs = new URLSearchParams({ provider: params.provider })
      if (params.clientId) qs.set('client_id', params.clientId)
      if (params.clientSecret) qs.set('client_secret', params.clientSecret)
      return apiFetch<{ auth_url: string; state: string }>(`/api/oauth/start?${qs}`)
    },
  })
}

export function useCompleteOAuth() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (params: { tempId: string; name: string; email?: string }) =>
      apiFetch<{ accountId: string; email: string }>('/api/oauth/complete', {
        method: 'POST',
        body: JSON.stringify({ temp_id: params.tempId, name: params.name, email: params.email }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['accounts'] }),
  })
}

// ---------- Messages hooks ----------

export interface FolderInfo {
  id: number
  path: string
  displayName: string
  role: string | null
  messageCount: number
  unreadCount: number
  syncEnabled: boolean
  lastSyncedAt: string | null
}

export interface MessageSummary {
  id: number
  uid: number
  folderId?: number
  subject: string
  fromAddress: string
  fromEmail: string
  dateEpoch: number | null
  date: string
  flags: string
  snippet: string
  hasAttachments: boolean
  folderPath: string
}

export interface MessageDetail extends MessageSummary {
  toAddresses: string
  ccAddresses: string
  bodyText: string | null
  bodyHtml: string | null
  bodyFetched: boolean
  threadId: string | null
}

export function useFolders(accountId: string | undefined) {
  return useQuery({
    queryKey: ['folders', accountId],
    queryFn: () => apiFetch<FolderInfo[]>(`/api/folders?account_id=${accountId}`),
    enabled: !!accountId,
  })
}

export function useMessages(accountId: string | undefined, folderId?: number, limit?: number) {
  const qs = new URLSearchParams()
  if (accountId) qs.set('account_id', accountId)
  if (folderId != null) qs.set('folder_id', String(folderId))
  if (limit != null) qs.set('limit', String(limit))
  return useQuery({
    queryKey: ['messages', accountId, folderId, limit],
    queryFn: () => apiFetch<MessageSummary[]>(`/api/messages?${qs}`),
    enabled: !!accountId && folderId != null,
  })
}

export function useMessage(accountId: string | undefined, folderId: number | undefined, uid: number | undefined) {
  return useQuery({
    queryKey: ['message', accountId, folderId, uid],
    queryFn: () => apiFetch<MessageDetail>(`/api/messages/${accountId}/${folderId}/${uid}`),
    enabled: !!accountId && folderId != null && uid != null,
  })
}

export function useSearchMessages(accountId: string | undefined, query: string, limit?: number) {
  const qs = new URLSearchParams()
  if (accountId) qs.set('account_id', accountId)
  if (query) qs.set('query', query)
  if (limit != null) qs.set('limit', String(limit))
  return useQuery({
    queryKey: ['messages-search', accountId, query, limit],
    queryFn: () => apiFetch<MessageSummary[]>(`/api/messages/search?${qs}`),
    enabled: !!accountId && query.length > 0,
  })
}

// ---------- Types ----------

export interface CreateAccountRequest {
  name: string
  imapHost: string
  imapPort: number
  smtpHost?: string
  smtpPort: number
  smtpUseSsl: boolean
  username: string
  authType: string
  password?: string
  provider: string
}
