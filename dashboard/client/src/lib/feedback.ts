// Helpers for the feedback sheet, kept out of the component file for fast refresh.

export const FEEDBACK_MAX = 2000

/** Message length in code points, matching the server and the relay (an emoji counts once). */
export function codePoints(s: string): number {
  return Array.from(s).length
}

/** UUID v4. crypto.randomUUID only exists in secure contexts, and the dashboard can be served over plain HTTP on a LAN. */
export function newClientId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const b = crypto.getRandomValues(new Uint8Array(16))
  b[6] = (b[6] & 0x0f) | 0x40
  b[8] = (b[8] & 0x3f) | 0x80
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}

/** Browser family and major version only, e.g. "Edge 142". */
export function coarseBrowser(ua: string = navigator.userAgent): string | undefined {
  const rules: Array<[string, RegExp]> = [
    ['Edge', /Edg\/(\d+)/],
    ['Opera', /OPR\/(\d+)/],
    ['Firefox', /Firefox\/(\d+)/],
    ['Chrome', /Chrome\/(\d+)/],
    ['Safari', /Version\/(\d+).*Safari/],
  ]
  for (const [name, re] of rules) {
    const m = ua.match(re)
    if (m) return `${name} ${m[1]}`
  }
  return undefined
}
