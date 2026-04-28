# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Ultimate IMAP MCP, please **do not** open a public issue. Instead, report it privately:

- **GitHub Security Advisories:** https://github.com/shahabmokhtari/ultimate-imap-mcp/security/advisories/new
- **Email:** Open a private security advisory via GitHub (preferred) so we can coordinate a fix before public disclosure.

Please include:

- A description of the issue and its potential impact
- Steps to reproduce (proof-of-concept code, if available)
- The version / commit you tested against
- Suggested mitigation, if known

We aim to acknowledge reports within **72 hours** and provide a fix or mitigation timeline within **7 days**.

## Supported Versions

This project is in active development. Security fixes are applied to the latest `main` branch. Use the latest snapshot release or build from source.

## Sensitive Data Handling

Ultimate IMAP MCP processes email content, account credentials, and OAuth tokens. The following protections are in place:

- **OAuth tokens:** Stored encrypted in `~/.ultimate-imap-mcp/accounts.json` (configurable). Never committed to the repo.
- **App passwords:** Resolved from environment variables (e.g. `${ACCOUNT_PERSONAL_PASSWORD}`) — never written to disk.
- **Email cache:** Stored in local SQLite (`~/.ultimate-imap-mcp/cache.db`) with WAL mode. Never transmitted off-device unless you explicitly configure an OTLP exporter or external LLM provider.
- **HTML email rendering:** Sanitized with DOMPurify; remote resources blocked by default; plain-text view is the default.
- **No telemetry by default:** No outbound calls except to your configured IMAP servers, OAuth providers (Google/Microsoft/Yahoo), and any LLM provider you enable.

## Threat Model

- **In scope:** Auth bypass, credential leakage, IMAP injection, queue corruption, dashboard XSS/CSRF, log/cache exposure of sensitive content.
- **Out of scope:** Compromise of the underlying IMAP server, OS-level attacks, attacks requiring local filesystem access (the threat model assumes the host is trusted).

## Best Practices for Users

- Use **OAuth** (PKCE flow, no client secret) over app passwords where possible.
- Set restrictive permissions on `~/.ultimate-imap-mcp/` (`chmod 700`).
- Run the dashboard on `localhost` unless you've configured `dashboard_auth`.
- Review `accounts.json` permissions periodically.
- When sharing logs for bug reports, redact account IDs and message UIDs.
