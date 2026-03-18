# Ultimate IMAP MCP

A batteries-included MCP server for email. Works with any IMAP provider (Gmail, Outlook, Fastmail, ProtonMail, Yahoo, self-hosted, etc.).

Unlike every other email MCP server out there, this one has a **local cache**, an **operation queue with undo**, a **web dashboard**, and **LLM-powered email analysis**.

## Why This Exists

Every existing IMAP MCP server hits the IMAP server on every single tool call. No caching, no queuing, no UI, no intelligence. This project fixes all of that.

## Key Features

- **Local SQLite cache with full-text search** - instant search, no IMAP round-trips
- **IMAP IDLE + polling** - real-time inbox sync, configurable per folder
- **Operation queue** - all writes are queued with priority tiers, retry logic, and undo
- **Send with undo** - emails are queued, not sent immediately. Cancel within a configurable window
- **Web dashboard** - account management, OAuth flows, sync monitoring, queue viewer, bulk ops
- **LLM analysis** - spam scoring, category labeling, priority detection, custom rules
- **Bulk operations with preview** - query emails by any criteria (including LLM scores), preview results, then execute
- **Mailbox reports** - volume trends, top senders, category breakdown, spam analysis
- **Multi-account** - first-class support for multiple email accounts
- **Thread reconstruction** - groups messages into conversations from headers
- **Provider quirk profiles** - auto-adapts folder names, auth methods, and search capabilities per provider
- **Token budget awareness** - configurable response sizes so you don't blow context windows

## Quick Start

```bash
# Install and run setup wizard
npx ultimate-imap-mcp setup

# Or with config file
npx ultimate-imap-mcp --config ./config.yaml

# Add to Claude Code
claude mcp add ultimate-imap-mcp -- npx ultimate-imap-mcp

# Open the dashboard
open http://localhost:3847
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md) - system design and component details
- [Data Model](docs/DATA_MODEL.md) - SQLite schema and indexes
- [Project Plan](docs/PROJECT_PLAN.md) - phased implementation plan with task lists
- [Feature Matrix](docs/FEATURE_MATRIX.md) - comparison with existing email MCP servers

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

See [config.example.yaml](config.example.yaml) for all options. Accounts can also be configured via the web dashboard.

## License

MIT
