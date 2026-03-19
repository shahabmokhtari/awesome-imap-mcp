# Architecture

## High-Level Overview

```
+-------------------+       MCP Protocol (stdio/HTTP)       +------------------------+
|                   | <-----------------------------------> |                        |
|  Claude / LLM     |       Tool calls & responses          |   MCP Server (Core)    |
|  (any MCP client) |                                       |                        |
+-------------------+                                       +----+---+---+---+------+
                                                                 |   |   |   |
                                                    +------------+   |   |   +----------+
                                                    |                |   |              |
                                              +-----v----+   +------v---v--+   +-------v------+
                                              |  Cache    |   |  Operation  |   |  LLM Analysis|
                                              |  Engine   |   |  Queue      |   |  Pipeline    |
                                              | (SQLite)  |   | (SQLite)    |   |              |
                                              +-----+-----+   +------+------+   +-------+------+
                                                    |                |                   |
                                              +-----v-----+   +-----v------+            |
                                              |  Sync      |   |  Queue     |            |
                                              |  Manager   |   |  Worker    |            |
                                              | (IDLE +    |   | (flush     |            |
                                              |  polling)  |   |  cycle)    |            |
                                              +-----+------+   +-----+------+            |
                                                    |                |                   |
                                                    +-------+--------+                   |
                                                            |                            |
                                                    +-------v--------+                   |
                                                    |  IMAP/SMTP     |                   |
                                                    |  Provider      |<------------------+
                                                    |  Abstraction   |  (fetches emails
                                                    |  Layer         |   for analysis)
                                                    +-------+--------+
                                                            |
                                                    +-------v--------+
                                                    |  IMAP Server   |
                                                    |  (Gmail, O365, |
                                                    |   Fastmail,    |
                                                    |   ProtonMail,  |
                                                    |   etc.)        |
                                                    +----------------+

+-------------------+       HTTP (localhost)        +------------------------+
|                   | <-----------------------------------> |                        |
|  Web Dashboard    |       REST API + WebSocket            |   Dashboard Server     |
|  (React SPA)      |                                       |   (Express)            |
+-------------------+                                       +------------------------+
                                                                      |
                                                              reads from shared
                                                              SQLite database
```

## Component Details

### 1. MCP Server (Core)

The main process. Exposes MCP tools over stdio (for Claude Code/Desktop) or HTTP+SSE (for remote clients).

Responsibilities:
- Register and expose all MCP tools
- Route tool calls to appropriate subsystems
- Manage account lifecycle
- Start/stop background services (sync, queue worker, dashboard)

Transport: stdio (default for Claude Code) and HTTP+SSE (for remote/web clients).

### 2. Cache Engine (SQLite + FTS5)

Single SQLite database storing all cached email data. FTS5 virtual table for full-text search over subject + body.

Key behaviors:
- Search queries hit the local cache first (instant)
- Cache miss triggers a live IMAP SEARCH, results are cached
- Headers-only sync by default, bodies fetched on demand (lazy)
- Each folder tracks `last_synced_uid` for incremental sync
- Configurable max cache age per folder (e.g., INBOX: 5 min, Archive: 1 hour)

Why SQLite:
- Zero config, single file, embedded
- FTS5 is fast enough for personal email volumes (100k+ messages)
- WAL mode allows concurrent reads from dashboard while MCP writes
- Portable across platforms

### 3. Sync Manager

Background service maintaining cache freshness.

Strategies:
- **IMAP IDLE** for INBOX (real-time push notifications from server)
- **Periodic polling** for other folders (configurable interval, default 5 min)
- **On-demand sync** via `sync_now` MCP tool (user-triggered)
- **Incremental sync** using IMAP UID ranges (only fetch new messages)
- **Full resync** available via MCP tool or dashboard (rebuilds cache)

Handles reconnection with exponential backoff on connection drops.

### 4. Operation Queue

SQLite-backed queue for write operations. All mutating IMAP/SMTP operations go through the queue.

Priority tiers:
- **P0 (near-immediate, flush every 2s):** send, reply, forward
- **P1 (batched, flush every 30s):** delete, move, mark read/unread, flag, label
- **P2 (background, flush every 5min):** bulk operations, cleanup

Features:
- Each operation gets a `pending_id` returned to the LLM immediately
- `cancel_operation` tool to cancel pending operations
- `confirm_send` / `cancel_send` for email sends (undo window)
- Operations are idempotent where possible (uses IMAP UIDs)
- Failed operations retry with backoff, then move to dead-letter

### 5. LLM Analysis Pipeline

Processes emails through an LLM for classification, scoring, and labeling.

Modes:
- **On-demand** via MCP tool: `analyze_email(uid)`, `analyze_folder(folder, criteria)`
- **Batch via dashboard:** user triggers analysis on a set of emails
- **Background rules:** user-defined rules (via dashboard) that auto-run on new emails

Capabilities:
- Spam scoring (0-100) with explanation
- Category labeling (newsletter, transactional, personal, work, spam, etc.)
- Priority scoring
- Custom user-defined labels and rules
- Summary generation for long threads

Architecture:
- Uses the Anthropic API (configurable model, default: Haiku for cost efficiency)
- Results stored in the `llm_analysis` table in SQLite
- Token usage tracked and displayed in dashboard
- Rate-limited to avoid API cost surprises (configurable daily/monthly budget)

### 6. Provider Abstraction Layer

Normalizes IMAP quirks across providers.

Provider profiles (auto-detected from IMAP host or manually configured):
- **Gmail:** folder names (`[Gmail]/Sent Mail`, `[Gmail]/Trash`), XOAUTH2, search extensions
- **O365/Outlook:** folder names (`Sent Items`, `Deleted Items`), Modern Auth
- **Fastmail:** JMAP-aware folder names, fast SEARCH
- **ProtonMail Bridge:** localhost IMAP, self-signed certs, limited SEARCH
- **Yahoo:** app password requirement, non-standard folders
- **Generic IMAP:** sensible defaults, folder auto-discovery

Each profile maps:
- Standard folder names (inbox, sent, drafts, trash, spam, archive) to provider-specific paths
- Search capability flags (which SEARCH extensions are supported)
- Auth method (password, app password, XOAUTH2, client cert)
- Known rate limits and connection limits

### 7. Web Dashboard (React SPA + Express API)

Runs on localhost (configurable port, default 3847). Provides:

**Account Management:**
- Add/remove/edit email accounts
- OAuth2 authentication flows (browser-based)
- App password configuration
- Connection status and health checks

**Monitoring:**
- Sync status per folder per account (last synced, message count, cache staleness)
- Operation queue: pending, in-progress, completed, failed
- Error log with timestamps and retry status
- IMAP connection health (connected, reconnecting, failed)

**LLM Analysis:**
- Trigger analysis runs on selected folders/date ranges
- View analysis results: spam scores, categories, labels per email
- Configure analysis rules (auto-label, auto-archive, etc.)
- Token usage and cost tracking
- Configure LLM provider and API key

**Bulk Operations (with preview):**
- Build queries: "emails where spam_score < 60", "emails labeled 'newsletter' older than 30 days"
- Preview matching emails in a table before executing
- Execute operations: delete, move, archive, label, mark read
- Progress tracking for long-running bulk ops
- Undo for recently completed bulk ops (within time window)

**Mailbox Reports:**
- Email volume over time (daily/weekly/monthly)
- Top senders
- Category distribution (pie chart)
- Response time analysis
- Storage usage per folder
- Spam trend analysis

### 8. Configuration

YAML-based configuration with sensible defaults. Can also be fully managed via Web Dashboard.

```yaml
# config.yaml
server:
  transport: stdio          # stdio | http
  http_port: 3846           # only if transport: http
  dashboard_port: 3847
  dashboard_enabled: true

accounts:
  - name: personal
    host: imap.gmail.com
    port: 993
    username: shahab@gmail.com
    auth: oauth2             # oauth2 | password | app_password
    smtp:
      host: smtp.gmail.com
      port: 465
      use_ssl: true
    provider: gmail          # gmail | outlook | fastmail | protonmail | yahoo | generic
    sync:
      idle_folders: [INBOX]
      poll_interval: 300     # seconds, for non-IDLE folders
      max_cache_age: 86400   # seconds, for full body cache
      folders: [INBOX, "[Gmail]/Sent Mail", "[Gmail]/Drafts"]

  - name: work
    host: outlook.office365.com
    port: 993
    username: shahab@company.com
    auth: oauth2
    provider: outlook

cache:
  db_path: ~/.ultimate-imap-mcp/cache.db
  max_size_mb: 500
  body_fetch: lazy           # lazy | eager

queue:
  p0_flush_interval: 2      # seconds
  p1_flush_interval: 30
  p2_flush_interval: 300
  send_undo_window: 10      # seconds to cancel a send

llm:
  enabled: true
  provider: anthropic        # anthropic | openai | local
  model: claude-haiku-4-5-20251001
  api_key_env: ANTHROPIC_API_KEY
  daily_token_budget: 1000000
  auto_analyze_new: false    # auto-analyze incoming emails
  rules: []                  # defined via dashboard
```
