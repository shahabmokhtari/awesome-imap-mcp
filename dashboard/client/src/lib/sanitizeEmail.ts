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

  // Scrub remote URLs from <style> blocks (@import and url() for CSS exfiltration)
  doc.querySelectorAll('style').forEach(el => {
    el.textContent = (el.textContent || '')
      .replace(/@import\s+[^;]+;?/gi, '/* @import blocked */')
      .replace(/url\s*\([^)]*\)/gi, 'none')
  })

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

  // Strip javascript: URLs from all href attributes
  doc.querySelectorAll('a[href]').forEach(a => {
    const href = a.getAttribute('href') || ''
    if (href.trim().toLowerCase().startsWith('javascript:')) {
      a.removeAttribute('href')
    }
  })

  // Inject a tiny script to intercept link clicks and postMessage to the parent.
  // The iframe must use sandbox="allow-scripts" (without allow-same-origin, this
  // is safe — the script cannot access the parent's DOM, cookies, or localStorage).
  const interceptScript = doc.createElement('script')
  interceptScript.textContent = `
    document.addEventListener('click', function(e) {
      var a = e.target;
      while (a && a.tagName !== 'A') a = a.parentElement;
      if (a && a.href) {
        e.preventDefault();
        window.parent.postMessage({ type: 'email-link-click', url: a.href }, '*');
      }
    });
  `
  doc.body.appendChild(interceptScript)

  return doc.documentElement.outerHTML
}
