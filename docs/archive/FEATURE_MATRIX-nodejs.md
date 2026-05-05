# Feature Matrix: Awesome IMAP MCP vs Community MCP Servers

## Competitor Analysis

| Feature | non-dirty/imap-mcp | ai-zerolab/mcp-email-server | marlinjai/email-mcp | **Awesome IMAP MCP** |
|---|---|---|---|---|
| **Language** | Python | Python | TypeScript (Node) | TypeScript (Node) |
| **IMAP Support** | Yes | Yes | Yes (fallback) | Yes (primary) |
| **Gmail REST API** | No | No | Yes | No (IMAP-only, universal) |
| **Outlook Graph API** | No | No | Yes | No (IMAP-only, universal) |
| **Multi-account** | No | Yes (TOML config) | Yes (setup wizard) | Yes (YAML config + Web UI) |
| **OAuth2** | Yes (Gmail) | Yes (Gmail) | Yes (Gmail, Outlook) | Yes (Web UI flow, any provider) |
| **App Password** | Yes | Yes | Yes (iCloud) | Yes |
| **Search** | Basic IMAP SEARCH | Basic IMAP SEARCH | Provider-native + IMAP | Local FTS5 + IMAP fallback |
| **Read Messages** | Yes (text, HTML, attachments) | Yes | Yes (compact by default) | Yes (cached, token-aware) |
| **Send/Reply/Forward** | Draft + Reply | Yes (SMTP) | Yes | Yes (queued with undo) |
| **Move/Delete** | Yes | Yes | Yes (batch) | Yes (queued, batch) |
| **Mark Read/Unread/Flag** | Yes | Yes | Yes (batch) | Yes (queued, batch) |
| **Label Management** | No | No | Limited | Yes |
| **Batch Operations** | No | No | Yes (up to 1000) | Yes (with preview via Web UI) |
| **Drafts** | Yes | Yes | Yes | Yes |
| **Attachments** | Read | Optional download | Read | Read + local temp storage |
| **Credential Storage** | YAML config | Env vars / TOML | AES-256-GCM encrypted | AES-256-GCM encrypted (SQLite) |
| **Local Cache** | No | No | No | **Yes (SQLite + FTS5)** |
| **IMAP IDLE** | No | No | No | **Yes (real-time sync)** |
| **Operation Queue** | No | No | No | **Yes (with cancel/undo)** |
| **Web Dashboard** | No | No | No | **Yes** |
| **OAuth via Web UI** | No | No | No | **Yes** |
| **LLM Integration** | Planned "Learning Layer" | No | No | **Yes (spam detection, labeling)** |
| **Mailbox Reports** | No | No | No | **Yes (stats, trends)** |
| **Bulk Ops with Preview** | No | No | No | **Yes (query-then-execute)** |
| **Thread Reconstruction** | No | No | No | **Yes (In-Reply-To/References)** |
| **Token Budget Awareness** | No | No | Compact mode (~20KB) | **Yes (configurable)** |
| **Rate Limiting** | No | No | No | **Yes (per-provider)** |
| **Provider Quirk Profiles** | No | Auto-detect sent folder | Provider-native APIs | **Yes (folder mapping, search caps)** |
| **Docker Support** | No | Yes | No | Yes |
| **Config Format** | YAML | Env vars / TOML | Encrypted JSON | YAML + Web UI |
| **Self-signed Cert Support** | No | Yes (ProtonMail Bridge) | No | Yes |

## Key Differentiators

1. **Cache layer** - no existing MCP server caches anything locally. Every operation is a live IMAP round-trip.
2. **Operation queue with undo** - no existing server queues operations or allows cancellation.
3. **Web dashboard** - no existing server has any UI. All are CLI/config-file only.
4. **LLM-powered email classification** - no existing server integrates LLM analysis for spam/labeling.
5. **Bulk operations with preview** - marlinjai supports batch delete/move but without preview. We add a query-then-confirm workflow.
6. **Thread reconstruction** - none of them reconstruct conversation threads from headers.
