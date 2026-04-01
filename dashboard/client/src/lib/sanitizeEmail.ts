import DOMPurify from 'dompurify'

/**
 * Sanitizes email HTML for safe rendering in an iframe.
 * Strips scripts, event handlers, and optionally blocks remote resources.
 */
export function sanitizeEmailHtml(html: string, allowRemoteImages: boolean = false): string {
  const clean = DOMPurify.sanitize(html, {
    WHOLE_DOCUMENT: true,
    FORCE_BODY: false,
    ADD_TAGS: ['style', 'link'],
    FORBID_TAGS: ['script', 'iframe', 'object', 'embed', 'form', 'input', 'textarea', 'button', 'select'],
    FORBID_ATTR: [
      'onerror', 'onload', 'onclick', 'onmouseover', 'onmouseout', 'onmousedown',
      'onmouseup', 'onfocus', 'onblur', 'onsubmit', 'onchange', 'onkeydown',
      'onkeyup', 'onkeypress', 'oncontextmenu', 'ondblclick',
    ],
  })

  if (allowRemoteImages) {
    return clean
  }

  const parser = new DOMParser()
  const doc = parser.parseFromString(clean, 'text/html')

  doc.querySelectorAll('link').forEach(el => el.remove())

  doc.querySelectorAll('img').forEach(img => {
    const src = img.getAttribute('src') || ''
    if (src.startsWith('http:') || src.startsWith('https:') || src.startsWith('//')) {
      img.removeAttribute('src')
      img.setAttribute('alt', '[Remote image blocked]')
      img.setAttribute('title', 'Remote images are blocked for security. Use the toggle to load them.')
    }
  })

  doc.querySelectorAll('[style]').forEach(el => {
    const style = el.getAttribute('style') || ''
    if (/url\s*\(/i.test(style)) {
      el.setAttribute('style', style.replace(/url\s*\([^)]*\)/gi, 'none'))
    }
  })

  return doc.documentElement.outerHTML
}
