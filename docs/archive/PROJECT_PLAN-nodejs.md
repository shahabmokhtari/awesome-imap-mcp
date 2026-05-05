# Project Plan

## Tech Stack

- **Runtime:** Node.js 22+ (TypeScript)
- **MCP SDK:** `@modelcontextprotocol/sdk`
- **Database:** better-sqlite3 (synchronous, fast, WAL mode)
- **IMAP Client:** imapflow (modern, Promise-based, IDLE support)
- **SMTP Client:** nodemailer
- **Dashboard Backend:** Express.js + WebSocket (ws)
- **Dashboard Frontend:** React + Vite + Tailwind CSS + shadcn/ui
- **LLM Client:** @anthropic-ai/sdk (with support for OpenAI-compatible endpoints)
- **Encryption:** Node.js crypto (AES-256-GCM)
- **Config:** yaml (js-yaml)
- **Testing:** vitest
- **Bundling:** tsup (for MCP server), Vite (for dashboard)
- **Package Manager:** pnpm

## Project Structure

```
awesome-imap-mcp/
  docs/                          # project documentation
  packages/
    core/                        # shared types, config, db, encryption
      src/
        config.ts                # YAML config loader + validation
        db.ts                    # SQLite connection + migrations
        schema.ts                # table definitions
        migrations/              # numbered migration files
        encryption.ts            # AES-256-GCM for credentials
        types.ts                 # shared TypeScript types
        provider-profiles.ts     # provider quirk mappings
    imap-client/                 # IMAP/SMTP abstraction layer
      src/
        imap-connection.ts       # imapflow wrapper with reconnect
        smtp-connection.ts       # nodemailer wrapper
        folder-mapper.ts         # maps standard roles to provider paths
        message-parser.ts        # MIME parsing, thread extraction
        sync-manager.ts          # IDLE + polling + incremental sync
    mcp-server/                  # MCP tool definitions and server
      src/
        server.ts                # MCP server setup (stdio + HTTP)
        tools/
          search.ts              # search_emails (cache-first + IMAP fallback)
          read.ts                # get_message, get_thread
          compose.ts             # send_email, reply, forward (queued)
          organize.ts            # move, delete, mark, flag, label (queued)
          folders.ts             # list_folders, get_folder_stats
          sync.ts                # sync_now, get_sync_status
          queue.ts               # confirm_send, cancel_operation, list_pending
          analyze.ts             # analyze_email, analyze_folder, get_analysis
          reports.ts             # mailbox_report, top_senders, category_breakdown
          accounts.ts            # list_accounts, get_account_status
    queue/                       # operation queue + worker
      src/
        queue-manager.ts         # enqueue, dequeue, cancel, confirm
        queue-worker.ts          # flush loop with priority tiers
        operations/              # per-operation executors
          send.ts
          delete.ts
          move.ts
          flag.ts
          bulk.ts
    llm/                         # LLM analysis pipeline
      src/
        analyzer.ts              # core analysis engine
        prompts.ts               # prompt templates for each analysis type
        rule-engine.ts           # evaluate and execute llm_rules
        budget-tracker.ts        # token/cost tracking and limits
    dashboard/                   # web UI
      server/
        index.ts                 # Express + WebSocket server
        routes/
          accounts.ts            # CRUD + OAuth flows
          sync.ts                # sync status, trigger sync
          queue.ts               # pending ops, cancel, confirm
          analysis.ts            # trigger analysis, view results
          bulk.ts                # bulk query builder, preview, execute
          reports.ts             # mailbox stats and charts
          settings.ts            # config management
      client/                    # React SPA (Vite)
        src/
          pages/
            Dashboard.tsx        # overview: sync status, queue, errors
            Accounts.tsx         # account management + OAuth
            Analysis.tsx         # LLM analysis results + rules
            BulkOps.tsx          # query builder + preview + execute
            Reports.tsx          # charts and stats
            Queue.tsx            # operation queue viewer
            Settings.tsx         # config editor
          components/
            ...
  config.example.yaml
  package.json                   # pnpm workspace root
  pnpm-workspace.yaml
  tsconfig.base.json
  README.md
```

## Phases

### Phase 1: Foundation (weeks 1-2)

Goal: Basic MCP server that can connect to an IMAP account, cache messages, and expose read-only tools.

Tasks:
- [ ] Initialize monorepo with pnpm workspaces and shared tsconfig
- [ ] Implement `core` package: config loader, SQLite setup, migrations, types
- [ ] Implement `imap-client`: connection management, folder listing, message fetching, header parsing
- [ ] Implement provider profiles for Gmail, Outlook, Fastmail, generic IMAP
- [ ] Implement cache engine: headers-only sync, incremental sync via UIDs
- [ ] Implement FTS5 setup with sync triggers
- [ ] Implement thread reconstruction from In-Reply-To/References headers
- [ ] MCP tools: `list_folders`, `search_emails` (cache-first), `get_message`, `get_thread`
- [ ] Token-budget-aware responses: `summary_only` and `max_results` params on search
- [ ] Unit tests for all modules, integration test with a real Gmail account
- [ ] `config.example.yaml` with documentation

Deliverable: `claude mcp add` this server and search/read emails from any IMAP provider.

### Phase 2: Write Operations + Queue (weeks 3-4)

Goal: Send, reply, delete, move, flag with operation queue and undo support.

Tasks:
- [ ] Implement `queue` package: SQLite-backed queue, priority tiers, flush worker
- [ ] Implement SMTP connection (nodemailer) with provider profile support
- [ ] MCP tools: `send_email`, `reply_to`, `forward`
- [ ] Implement send confirmation flow: queue -> pending -> confirm/cancel -> execute
- [ ] MCP tools: `delete_messages`, `move_messages`, `mark_read`, `mark_unread`, `flag`, `label`
- [ ] MCP tools: `confirm_send`, `cancel_operation`, `list_pending_operations`
- [ ] Batch operation support (multiple UIDs per call)
- [ ] Dead-letter handling for permanently failed operations
- [ ] Rate limiting per provider (configurable)
- [ ] Tests for queue lifecycle, retry logic, cancellation

Deliverable: Full read/write email client via MCP with safety rails.

### Phase 3: Real-Time Sync (week 5)

Goal: IMAP IDLE for real-time inbox updates, robust polling for other folders.

Tasks:
- [ ] Implement IMAP IDLE listener in sync-manager (imapflow supports this natively)
- [ ] Configurable IDLE folders (default: INBOX only)
- [ ] Polling loop for non-IDLE folders with configurable intervals
- [ ] Reconnection with exponential backoff
- [ ] `sync_now` MCP tool for on-demand sync
- [ ] `get_sync_status` MCP tool: per-folder last sync time, message counts
- [ ] Sync log table population
- [ ] Cache eviction: configurable max age, LRU for bodies

Deliverable: Cache stays fresh automatically, INBOX updates are near real-time.

### Phase 4: Web Dashboard (weeks 6-8)

Goal: Full web UI for account management, monitoring, and operations.

Tasks:
- [ ] Express server with REST API routes
- [ ] WebSocket for real-time updates (sync events, queue changes)
- [ ] React SPA scaffold with Vite + Tailwind + shadcn/ui
- [ ] **Accounts page:** list accounts, add/edit/remove, connection status indicators
- [ ] **OAuth flow:** browser-based OAuth2 for Gmail and Outlook (redirect back to dashboard)
- [ ] **App password flow:** form-based entry with test-connection button
- [ ] **Dashboard overview:** sync status cards, queue summary, recent errors
- [ ] **Queue page:** list pending/completed/failed operations, cancel button, retry button
- [ ] **Settings page:** edit config.yaml values, restart services

Deliverable: Manage accounts and monitor the MCP server via browser.

### Phase 5: LLM Analysis Pipeline (weeks 9-10)

Goal: LLM-powered email classification, spam detection, and auto-labeling.

Tasks:
- [ ] Implement `llm` package: Anthropic SDK integration, prompt templates
- [ ] Analysis types: spam_score, category, priority, summary
- [ ] MCP tools: `analyze_email`, `analyze_folder`, `get_analysis_results`
- [ ] Budget tracker: daily/monthly token usage, cost calculation, hard limits
- [ ] Rule engine: user-defined rules with triggers (on_new, manual, scheduled)
- [ ] Dashboard: **Analysis page** with results table, filters, sorting
- [ ] Dashboard: **Rule builder** UI for creating/editing LLM rules
- [ ] Dashboard: token usage chart, cost breakdown by model/day
- [ ] Support for OpenAI-compatible endpoints (for local LLMs like Ollama)

Deliverable: Ask Claude "what are my spam emails?" and get LLM-scored results.

### Phase 6: Bulk Operations + Reports (weeks 11-12)

Goal: Bulk operations with preview and mailbox analytics.

Tasks:
- [ ] Dashboard: **Bulk Ops page** with query builder
  - Filter by: folder, date range, sender, subject, has_attachments, flags
  - Filter by: spam_score range, category, LLM label (requires Phase 5)
  - Preview: paginated table of matching emails
  - Actions: delete, move, archive, label, mark read
  - Confirmation dialog with count and action summary
  - Progress bar for execution
  - Undo button (within configurable time window)
- [ ] Dashboard: **Reports page**
  - Email volume chart (line, daily/weekly/monthly)
  - Top senders table
  - Category distribution (pie/donut chart)
  - Spam score distribution (histogram)
  - Folder size breakdown (bar chart)
  - Account comparison (if multi-account)
- [ ] MCP tools: `mailbox_report`, `top_senders`, `category_breakdown`
- [ ] Export reports as CSV/JSON

Deliverable: Full mailbox intelligence and mass email management.

### Phase 7: Polish + Distribution (week 13+)

Tasks:
- [ ] npm package publishing (`npx awesome-imap-mcp` and `npx awesome-imap-mcp setup`)
- [ ] Interactive setup wizard (like marlinjai/email-mcp)
- [ ] Docker image with multi-arch support
- [ ] Documentation: README, setup guides per provider, MCP tool reference
- [ ] Claude Code plugin bundle (SKILL.md + mcp config)
- [ ] CI/CD: GitHub Actions for test, lint, build, publish
- [ ] Smithery integration for one-click install
- [ ] Security audit: credential storage, OAuth token handling, CSRF on dashboard
- [ ] Performance testing with 100k+ message mailboxes

## MCP Tool Reference (Complete)

### Read Operations
| Tool | Description |
|---|---|
| `list_accounts` | List configured email accounts and their connection status |
| `list_folders` | List folders for an account with message/unread counts |
| `search_emails` | Search emails (cache-first, IMAP fallback). Params: query, folder, from, to, date_range, max_results, summary_only |
| `get_message` | Get full message by account + UID. Fetches body if not cached |
| `get_thread` | Get all messages in a thread (reconstructed from headers) |
| `get_folder_stats` | Detailed stats for a folder: count, unread, size, date range |

### Write Operations (all queued)
| Tool | Description |
|---|---|
| `send_email` | Compose and queue a new email. Returns pending_id |
| `reply_to` | Reply to a message (queued). Params: uid, body, reply_all |
| `forward` | Forward a message (queued). Params: uid, to, body |
| `delete_messages` | Queue deletion. Params: uids[], folder |
| `move_messages` | Queue move. Params: uids[], from_folder, to_folder |
| `mark_read` | Queue mark as read. Params: uids[], folder |
| `mark_unread` | Queue mark as unread. Params: uids[], folder |
| `flag_messages` | Queue flag/unflag. Params: uids[], folder, flag, set |
| `label_messages` | Queue label add/remove. Params: uids[], label, action |

### Queue Management
| Tool | Description |
|---|---|
| `confirm_send` | Confirm a pending send operation. Params: pending_id |
| `cancel_operation` | Cancel a pending operation. Params: pending_id |
| `list_pending` | List all pending operations with status |

### Sync
| Tool | Description |
|---|---|
| `sync_now` | Trigger immediate sync for a folder or all folders |
| `get_sync_status` | Per-folder sync status: last synced, staleness, health |

### Analysis (Phase 5+)
| Tool | Description |
|---|---|
| `analyze_email` | Run LLM analysis on a specific email. Params: uid, type |
| `analyze_folder` | Batch LLM analysis on a folder. Params: folder, type, limit |
| `get_analysis` | Get cached analysis results. Params: uid or query filter |

### Reports (Phase 6+)
| Tool | Description |
|---|---|
| `mailbox_report` | Overall mailbox stats: volume, top senders, categories |
| `top_senders` | Top N senders by volume. Params: account, days, limit |
| `category_breakdown` | Email categories with counts (requires LLM analysis) |
