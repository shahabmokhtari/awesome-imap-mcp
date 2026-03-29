import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'

class ApiError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function apiFetch<T>(url: string, options?: RequestInit): Promise<T> {
  const token = localStorage.getItem('dashboard_token')
  const headers: HeadersInit = { 'Content-Type': 'application/json' }
  if (token) {
    headers['Authorization'] = `Bearer ${token}`
  }
  const res = await fetch(url, { ...options, headers: { ...headers, ...options?.headers } })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new ApiError(body?.error || body?.message || `API error: ${res.status}`, res.status)
  }
  if (res.status === 204) return undefined as T
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

export function useTriggerSyncAll() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () =>
      apiFetch<{ triggered: number; total: number; errors: string[] }>('/api/sync/trigger-all', {
        method: 'POST',
      }),
    onSuccess: () => {
      setTimeout(() => qc.invalidateQueries({ queryKey: ['sync-status'] }), 2000)
    },
  })
}

export interface SyncLogEntry {
  id: number
  accountId: string
  folderId: number | null
  syncType: string
  status: string
  messagesSynced: number
  errorMessage: string | null
  startedAt: string
  completedAt: string | null
  durationMs: number | null
}

export function useSyncLogs(accountId?: string) {
  return useQuery({
    queryKey: ['sync-logs', accountId],
    queryFn: () => {
      const params = new URLSearchParams()
      if (accountId) params.set('account_id', accountId)
      params.set('limit', '30')
      return apiFetch<SyncLogEntry[]>(`/api/sync/logs?${params}`)
    },
    refetchInterval: 5000,
  })
}

export function useQueue(status?: string) {
  const url = status ? `/api/queue?status=${encodeURIComponent(status)}` : '/api/queue'
  return useQuery({
    queryKey: ['queue', status],
    queryFn: () => apiFetch<Array<Record<string, unknown>>>(url),
    refetchInterval: 5000,
  })
}

export function useCancelOperation() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<{ id: string; cancelled: boolean }>(`/api/queue/${id}/cancel`, { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['queue'] }),
  })
}

export function useConfirmOperation() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<{ id: string; confirmed: boolean }>(`/api/queue/${id}/confirm`, { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['queue'] }),
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

export function useLogs(params: { levels?: string; search?: string; page?: number; page_size?: number; scope?: string; instance_id?: string; live_only?: boolean }) {
  const qs = new URLSearchParams()
  if (params.levels) qs.set('level', params.levels)
  if (params.search) qs.set('search', params.search)
  if (params.page != null) qs.set('page', String(params.page))
  if (params.page_size != null) qs.set('page_size', String(params.page_size))
  if (params.scope) qs.set('scope', params.scope)
  if (params.instance_id) qs.set('instance_id', params.instance_id)
  if (params.live_only) qs.set('live_only', 'true')
  return useQuery({
    queryKey: ['logs', params.levels, params.search, params.page, params.page_size, params.scope, params.instance_id, params.live_only],
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

// ---------- Server hooks ----------

export interface ServerInfo {
  instance_id: string
  uptime_seconds: number
  process_id: number
  version: string
  started_at: string
  transport: string
  dashboard_port: number
  http_port: number
}

export function useServerInfo() {
  return useQuery({
    queryKey: ['server-info'],
    queryFn: () => apiFetch<ServerInfo>('/api/server/info'),
    refetchInterval: 30000,
  })
}

export function useShutdownServer() {
  return useMutation({
    mutationFn: (data?: { delay_seconds?: number }) =>
      apiFetch<{ message: string; shutting_down: boolean }>('/api/server/shutdown', {
        method: 'POST',
        body: JSON.stringify(data ?? {}),
      }),
  })
}

export interface InstanceHeartbeat {
  instanceId: string
  processId: number
  cwd: string
  transport: string
  isDashboardHost: boolean
  isLeader: boolean
  startedAt: string
  lastHeartbeat: string
  accountsCount: number
  cpuTimeMs: number
  memoryMb: number
  shutdownRequested: boolean
}

export interface InstancesResponse {
  current: string
  instances: InstanceHeartbeat[]
}

export function useInstances() {
  return useQuery({
    queryKey: ['instances'],
    queryFn: () => apiFetch<InstancesResponse>('/api/server/instances'),
    refetchInterval: 10000,
  })
}

export function useShutdownInstance() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (instanceId: string) =>
      apiFetch<{ shutting_down: boolean }>(
        `/api/server/instances/${encodeURIComponent(instanceId)}/shutdown`,
        { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['instances'] }),
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
      apiFetch<unknown>(`/api/accounts/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['accounts'] }),
  })
}

export function useToggleAccountEnabled() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (params: { id: string; enabled: boolean }) =>
      apiFetch<{ id: string; enabled: boolean }>(
        `/api/accounts/${encodeURIComponent(params.id)}/toggle-enabled`,
        { method: 'POST', body: JSON.stringify({ enabled: params.enabled }) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['accounts'] }),
  })
}

export function useTestAccount() {
  return useMutation({
    mutationFn: (id: string) =>
      apiFetch<{ success: boolean; message: string }>(`/api/accounts/${encodeURIComponent(id)}/test`, { method: 'POST' }),
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
        `/api/accounts/${encodeURIComponent(id)}/fetch-recent`, { method: 'POST' }),
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
    mutationFn: (params: { provider: string; clientId?: string; clientSecret?: string }) =>
      apiFetch<{ auth_url: string; state: string }>('/api/oauth/start', {
        method: 'POST',
        body: JSON.stringify({
          provider: params.provider,
          client_id: params.clientId,
          client_secret: params.clientSecret,
        }),
      }),
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
    queryFn: () => apiFetch<FolderInfo[]>(`/api/folders?account_id=${encodeURIComponent(accountId!)}`),
    enabled: !!accountId,
  })
}

export function useMessages(accountId: string | undefined, folderId?: number, limit?: number, offset?: number) {
  const qs = new URLSearchParams()
  if (accountId) qs.set('account_id', accountId)
  if (folderId != null) qs.set('folder_id', String(folderId))
  if (limit != null) qs.set('limit', String(limit))
  if (offset != null && offset > 0) qs.set('offset', String(offset))
  return useQuery({
    queryKey: ['messages', accountId, folderId, limit, offset],
    queryFn: () => apiFetch<MessageSummary[]>(`/api/messages?${qs}`),
    enabled: !!accountId && folderId != null,
  })
}

export function useMessage(accountId: string | undefined, folderId: number | undefined, uid: number | undefined) {
  return useQuery({
    queryKey: ['message', accountId, folderId, uid],
    queryFn: () => apiFetch<MessageDetail>(`/api/messages/${encodeURIComponent(accountId!)}/${folderId}/${uid}`),
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

export function useFetchBody() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (params: { accountId: string; folderId: number; uid: number }) =>
      apiFetch<FetchBodyResult>(
        `/api/messages/${encodeURIComponent(params.accountId)}/${params.folderId}/${params.uid}/fetch-body`,
        { method: 'POST' }),
    onSuccess: (_, params) => {
      qc.invalidateQueries({ queryKey: ['message', params.accountId, params.folderId, params.uid] })
    },
    onError: (error) => {
      console.error('[useFetchBody] failed:', error)
    },
  })
}

export function useClearCache() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => apiFetch<{ deleted: number }>('/api/cache', { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['messages'] })
      qc.invalidateQueries({ queryKey: ['folders'] })
      qc.invalidateQueries({ queryKey: ['sync-status'] })
    },
    onError: (error) => {
      console.error('[useClearCache] failed:', error)
    },
  })
}

export function useClearAccountCache() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (accountId: string) =>
      apiFetch<{ deleted: number }>(`/api/cache/${encodeURIComponent(accountId)}`, { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['messages'] })
      qc.invalidateQueries({ queryKey: ['folders'] })
      qc.invalidateQueries({ queryKey: ['sync-status'] })
    },
    onError: (error) => {
      console.error('[useClearAccountCache] failed:', error)
    },
  })
}

export function useClearFolderCache() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (params: { accountId: string; folderId: number }) =>
      apiFetch<{ deleted: number }>(
        `/api/cache/${encodeURIComponent(params.accountId)}/${params.folderId}`,
        { method: 'DELETE' }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['messages'] })
      qc.invalidateQueries({ queryKey: ['folders'] })
      qc.invalidateQueries({ queryKey: ['sync-status'] })
    },
    onError: (error) => {
      console.error('[useClearFolderCache] failed:', error)
    },
  })
}

// ---------- Cache stats ----------

export interface CacheStats {
  totalMessages: number
  bodiesFetched: number
  dbSizeBytes: number
  dbSizeMb: number
  accounts: Array<{
    accountId: string
    accountName: string
    messageCount: number
    bodiesFetched: number
    oldestCachedAt: string | null
    newestCachedAt: string | null
  }>
}

export function useCacheStats() {
  return useQuery({
    queryKey: ['cache-stats'],
    queryFn: () => apiFetch<CacheStats>('/api/cache/stats'),
    refetchInterval: 30000,
  })
}

// ---------- LLM hooks ----------

export function useLlmModels(provider: string | undefined) {
  return useQuery({
    queryKey: ['llm-models', provider],
    queryFn: () => apiFetch<string[]>(`/api/llm/models?provider=${encodeURIComponent(provider ?? '')}`),
    enabled: !!provider,
  })
}

export interface LlmTestResult {
  response: string | null
  model: string
  duration_ms: number
}

export interface FetchBodyResult {
  id: number
  uid: number
  subject: string
  bodyText: string | null
  bodyHtml: string | null
  bodyFetched: boolean
}

export function useTestLlm() {
  return useMutation({
    mutationFn: (data: { prompt: string; provider?: string; model?: string }) =>
      apiFetch<LlmTestResult>('/api/llm/test', {
        method: 'POST',
        body: JSON.stringify(data),
      }),
  })
}

// ---------- Tools hooks ----------

export interface ToolParameterInfo {
  name: string
  type: string
  description: string
  required: boolean
  defaultValue: unknown
}

export interface ToolInfo {
  name: string
  description: string
  className: string
  methodName: string
  parameters: ToolParameterInfo[]
}

export function useTools() {
  return useQuery({
    queryKey: ['tools'],
    queryFn: () => apiFetch<ToolInfo[]>('/api/tools'),
  })
}

export interface ToolSuggestion {
  value: string | number
  label: string
  accountId?: string
}

export function useToolSuggestions() {
  return useQuery({
    queryKey: ['tool-suggestions'],
    queryFn: () => apiFetch<Record<string, ToolSuggestion[]>>('/api/tools/suggestions'),
    staleTime: 30000,
  })
}

export function useExecuteTool() {
  return useMutation({
    mutationFn: (params: { name: string; args: Record<string, unknown> }) =>
      apiFetch<unknown>(`/api/tools/${encodeURIComponent(params.name)}/execute`, {
        method: 'POST',
        body: JSON.stringify(params.args),
      }),
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
