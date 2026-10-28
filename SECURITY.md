# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Awesome IMAP MCP, please **do not** open a public issue. Instead, report it privately:

- **GitHub Security Advisories:** https://github.com/shahabmokhtari/awesome-imap-mcp/security/advisories/new
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

Awesome IMAP MCP processes email content, account credentials, and OAuth tokens. The following protections are in place:

- **OAuth tokens:** Stored encrypted in `~/.awesome-imap-mcp/accounts.json` (configurable). Never committed to the repo.
- **App passwords:** Either referenced from environment variables in `config.json` (e.g. `${ACCOUNT_PERSONAL_PASSWORD}`), or — when added through the dashboard or `add_account_imap` — stored in `accounts.json` encrypted with AES-256-GCM. The key is derived with PBKDF2 (100,000 iterations) from `AIMAP_PASSPHRASE` if set, otherwise from the machine ID. Set `AIMAP_PASSPHRASE` if the data directory could be copied to another machine.
- **Email cache:** Stored in local SQLite (`~/.awesome-imap-mcp/cache.db`) with WAL mode. Never transmitted off-device unless you explicitly configure an OTLP exporter or external LLM provider.
- **HTML email rendering:** Sanitized with DOMPurify and shown in a sandboxed iframe without same-origin access; remote resources blocked by default; plain-text view is the default.
- **Network exposure:** The MCP HTTP transport and dashboard bind to loopback (`127.0.0.1` / `::1`) by default and reject requests whose `Host` header isn't a loopback name, which defeats DNS rebinding. The MCP transport has **no authentication**; `server.bind_address` can widen the binding, and the server logs a warning when it does.
- **Agent-driven writes:** Mail changes go through an undoable operation queue. `download_attachment` will not replace an existing file unless the caller passes `overwrite: true`, so a prompt-injected email cannot clobber a shell profile or config file.
- **Protocol logging:** `server.log_protocol` records raw MCP traffic, including message bodies and credentials passed to tools. It is off by default; never enable it on shared machines or attach its output to bug reports.
- **Releases:** Binaries are published with `SHA256SUMS`; `install.sh` refuses to install a binary that doesn't match.
- **No telemetry by default:** No outbound calls except to your configured IMAP servers, OAuth providers (Google/Microsoft/Yahoo), and any LLM provider you enable.

## Threat Model

- **In scope:** Auth bypass, credential leakage, IMAP injection, queue corruption, dashboard XSS/CSRF, DNS rebinding, log/cache exposure of sensitive content, and **prompt injection** — an email instructing the agent to take a harmful action through the tools.
- **Out of scope:** Compromise of the underlying IMAP server, OS-level attacks, attacks requiring local filesystem access (the threat model assumes the host is trusted).

## Best Practices for Users

- Use **OAuth** (PKCE flow, no client secret) over app passwords where possible.
- Set restrictive permissions on `~/.awesome-imap-mcp/` (`chmod 700`).
- Keep `bind_address` at its default `localhost`. If you must expose the server, use an SSH tunnel or an authenticating reverse proxy, and set a dashboard PIN.
- Treat email content as untrusted input to your agent: prefer `confirm_mode: "explicit"` for sending, and review the queue before confirming bulk deletes.
- Review `accounts.json` permissions periodically.
- When sharing logs for bug reports, redact account IDs and message UIDs.
