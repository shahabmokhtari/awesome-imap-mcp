import { useQuery } from '@tanstack/react-query'

async function apiFetch<T>(url: string): Promise<T> {
  const token = localStorage.getItem('dashboard_token')
  const headers: HeadersInit = { 'Content-Type': 'application/json' }
  if (token) {
    headers['Authorization'] = `Bearer ${token}`
  }
  const res = await fetch(url, { headers })
  if (!res.ok) {
    throw new Error(`API error: ${res.status} ${res.statusText}`)
  }
  return res.json()
}

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
