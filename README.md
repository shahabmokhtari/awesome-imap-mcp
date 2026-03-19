# Ultimate IMAP MCP

A batteries-included MCP server for email. Works with any IMAP provider (Gmail, Outlook, Fastmail, ProtonMail, Yahoo, self-hosted, etc.).

Unlike every other email MCP server out there, this one has a **local cache**, an **operation queue with undo**, a **web dashboard**, and **LLM-powered email analysis**.

Built with **.NET 8+** and **MailKit** for maximum performance and cross-platform support.

## Why This Exists

Every existing IMAP MCP server hits the IMAP server on every single tool call. No caching, no queuing, no UI, no intelligence. This project fixes all of that.

## Key Features

- **Local SQLite cache with full-text search** - instant search, no IMAP round-trips
- **IMAP IDLE + polling** - real-time inbox sync, configurable per folder
- **Operation queue** - all writes are queued with priority tiers, retry logic, and undo
- **Send with undo** - emails are queued, not sent immediately. Cancel within a configurable window
- **Web dashboard (optional)** - account management, OAuth flows, sync monitoring, queue viewer, bulk ops
- **LLM analysis** - spam scoring, category labeling, priority detection, custom rules (via API, ACP, or in-context)
- **Bulk operations with preview** - query emails by any criteria (including LLM scores), preview results, then execute
- **Mailbox reports** - volume trends, top senders, category breakdown, spam analysis
- **Multi-account** - first-class support for multiple email accounts
- **Thread reconstruction** - groups messages into conversations from headers
- **Provider quirk profiles** - auto-adapts folder names, auth methods, and search capabilities per provider
- **Token budget awareness** - configurable response sizes so you don't blow context windows
- **Full observability** - metrics, logs, and tracing (OpenTelemetry export optional)

## Quick Start

```bash
# Install as a .NET global tool
dotnet tool install -g ultimate-imap-mcp

# Run with config file
ultimate-imap-mcp --config ./config.json

# Add to Claude Code
claude mcp add ultimate-imap-mcp -- ultimate-imap-mcp

# Open the dashboard (if enabled)
open http://localhost:3847
```

## Documentation

- [Design Specification](docs/superpowers/specs/2026-03-18-ultimate-imap-mcp-design.md) - full system design, architecture, and implementation plan
- [Data Model](docs/DATA_MODEL.md) - SQLite schema and indexes

## MCP Tools

### Read
- `list_accounts` / `list_folders` / `get_folder_stats`
- `search_emails` - cache-first full-text search with IMAP fallback
- `get_message` / `get_thread` - single message or full conversation

### Write (all queued with undo)
- `send_email` / `reply_to` / `forward`
- `delete_messages` / `move_messages` / `mark_read` / `mark_unread`
- `flag_messages` / `label_messages`

### Queue Management
- `confirm_send` / `cancel_operation` / `list_pending`

### Sync
- `sync_now` / `get_sync_status`

### LLM Analysis
- `analyze_email` / `analyze_folder` / `get_analysis`

### Reports
- `mailbox_report` / `top_senders` / `category_breakdown`

## Configuration

JSON-based configuration. See the [design spec](docs/superpowers/specs/2026-03-18-ultimate-imap-mcp-design.md#configuration) for all options. Accounts can also be configured via the web dashboard.

## License

MIT
