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
  const url = status ? `/api/queue?status=${encodeURIComponent(status)}` : '/api/queue'
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
    queryFn: () => apiFetch<FolderInfo[]>(`/api/folders?account_id=${encodeURIComponent(accountId!)}`),
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
  error?: string
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
