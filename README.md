# Ultimate IMAP MCP

A batteries-included MCP server for email. Works with any IMAP provider (Gmail, Outlook, Fastmail, ProtonMail, Yahoo, self-hosted, etc.).

Unlike every other email MCP server out there, this one has a **local cache**, an **operation queue with undo**, a **web dashboard**, and **LLM-powered email analysis**.

Built with **.NET 10** and **MailKit** for maximum performance and cross-platform support.

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
- **HTTP+SSE transport** - run as stdio (default) or HTTP server for remote MCP clients, or both

## Quick Start

### .NET Global Tool

```bash
# Install
dotnet tool install -g ultimate-imap-mcp

# Run with default config (~/.ultimate-imap-mcp/config.json)
ultimate-imap-mcp

# Run with specific config
ultimate-imap-mcp --config ./config.json

# Add to Claude Code
claude mcp add ultimate-imap-mcp -- ultimate-imap-mcp
```

### Docker

```bash
# Build and run
docker compose up -d

# Or run standalone
docker build -t ultimate-imap-mcp .
docker run -v ./config.json:/app/config.json -v imap-cache:/data -p 3847:3847 ultimate-imap-mcp
```

### From Source

```bash
git clone https://github.com/shahab1363/ultimate-imap-mcp.git
cd ultimate-imap-mcp
dotnet run --project src/UltimateImapMcp.McpServer -- --config ./config.example.json
```

## MCP Tools

### Accounts
| Tool | Description |
|------|-------------|
| `list_accounts` | List all configured email accounts and their status |
| `get_account_status` | Get detailed status for a specific email account |

### Folders
| Tool | Description |
|------|-------------|
| `list_folders` | List folders for an email account with message and unread counts |
| `get_folder_stats` | Get detailed stats for a specific folder |

### Search & Read
| Tool | Description |
|------|-------------|
| `search_emails` | Cache-first full-text search with IMAP fallback. Use `summary_only: true` first |
| `get_message` | Get a single message by UID with full body content |
| `get_thread` | Get a full conversation thread by thread ID |

### Compose (all queued with undo)
| Tool | Description |
|------|-------------|
| `send_email` | Compose and send a new email. Returns `pending_id` and `confirm_mode` |
| `reply_to` | Reply to an existing email message |
| `forward` | Forward an email message to one or more recipients |

### Organize (all queued with undo)
| Tool | Description |
|------|-------------|
| `delete_messages` | Delete one or more messages by UID from a folder |
| `move_messages` | Move messages from one folder to another |
| `mark_read` | Mark messages as read |
| `mark_unread` | Mark messages as unread |
| `flag_messages` | Flag or unflag messages |
| `label_messages` | Add or remove a label from messages |

### Queue Management
| Tool | Description |
|------|-------------|
| `confirm_send` | Confirm a pending send operation for immediate execution |
| `cancel_operation` | Cancel a pending or confirmed operation before execution |
| `list_pending` | List all pending and confirmed operations |

### Sync
| Tool | Description |
|------|-------------|
| `sync_now` | Trigger immediate sync for a folder or all folders |
| `get_sync_status` | Get sync status for all folders of an account |

### LLM Analysis
| Tool | Description |
|------|-------------|
| `analyze_email` | Analyze a single email for spam, category, priority, and summary |
| `analyze_folder` | Batch-analyze emails in a folder (respects token budget) |
| `get_analysis` | Get stored analysis for a specific email |
| `get_analysis_budget` | Check remaining daily token budget and monthly cost limit |

### Reports
| Tool | Description |
|------|-------------|
| `mailbox_report` | Generate volume trends, category breakdown, and top senders report |
| `top_senders` | Get top email senders ranked by volume |
| `category_breakdown` | Get email category distribution with counts |

## Configuration

Configuration is loaded from `config.json`. The default path is `~/.ultimate-imap-mcp/config.json`. Override with `--config <path>` or `UIMAP_CONFIG_PATH` env var.

See [`config.example.json`](config.example.json) for a full example.

### Server

```json
{
  "server": {
    "transport": "stdio",
    "http_port": 3846,
    "dashboard_port": 3847,
    "dashboard_enabled": false,
    "dashboard_auth": "pin"
  }
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `transport` | `"stdio"` | MCP transport mode: `"stdio"`, `"http"`, or `"both"` |
| `http_port` | `3846` | Port for HTTP+SSE MCP transport |
| `dashboard_port` | `3847` | Port for the web dashboard |
| `dashboard_enabled` | `false` | Enable the web dashboard |
| `dashboard_auth` | `null` | Dashboard auth mode: `"pin"` or `null` |

### Accounts

```json
{
  "accounts": [
    {
      "name": "personal",
      "imap_host": "imap.gmail.com",
      "imap_port": 993,
      "smtp_host": "smtp.gmail.com",
      "smtp_port": 465,
      "smtp_use_ssl": true,
      "username": "you@gmail.com",
      "auth_type": "app_password",
      "password": "${ACCOUNT_PERSONAL_PASSWORD}",
      "provider": "gmail",
      "confirm_mode": "implicit",
      "undo_window_seconds": 10,
      "sync": {
        "idle_folders": ["INBOX"],
        "poll_interval": 300,
        "folders": [
          { "path": "INBOX", "cache_window_days": 60 },
          { "path": "[Gmail]/Sent Mail", "cache_window_days": 14 }
        ]
      }
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `auth_type` | `"app_password"` | Auth method: `"app_password"` or `"oauth2"` |
| `provider` | `"generic"` | Provider profile: `"gmail"`, `"outlook"`, `"fastmail"`, `"protonmail"`, `"yahoo"`, `"generic"` |
| `confirm_mode` | `"implicit"` | Send confirmation: `"implicit"` (auto-send after delay) or `"explicit"` (wait for confirm) |
| `undo_window_seconds` | `10` | Seconds before implicit sends execute |
| `password` | - | Password or `${ENV_VAR}` reference |

### Cache

```json
{
  "cache": {
    "db_path": "~/.ultimate-imap-mcp/cache.db",
    "max_size_mb": 500,
    "default_window_days": 0,
    "max_body_age_days": 0
  }
}
```

### Queue

```json
{
  "queue": {
    "p0_flush_interval": 2,
    "p1_flush_interval": 30,
    "p2_flush_interval": 300,
    "send_undo_window": 10,
    "max_retries": 3
  }
}
```

### LLM Analysis

```json
{
  "llm": {
    "enabled": false,
    "provider": "anthropic",
    "model": "claude-haiku-4-5-20251001",
    "api_key_env": "ANTHROPIC_API_KEY",
    "daily_token_budget": 1000000,
    "monthly_cost_limit": 5.00,
    "auto_analyze_new": false
  }
}
```

Supported providers: `"anthropic"`, `"openai"`, `"acp_claude"`, `"acp_copilot"`, `"in_context"`.

### Metrics

```json
{
  "metrics": {
    "internal_retention_days": 7,
    "otlp_endpoint": null,
    "otlp_protocol": "grpc",
    "export_interval_seconds": 15
  }
}
```

## Web Dashboard

Enable with `"dashboard_enabled": true` in config. The dashboard runs on `dashboard_port` (default: 3847) and provides:

- **Accounts** - view, add, and test email account connections
- **Sync** - monitor sync status, trigger manual syncs
- **Queue** - view pending operations, confirm or cancel sends
- **Metrics** - system performance graphs and counters
- **Logs** - live log viewer with level filtering
- **Settings** - view current configuration

Optional PIN authentication protects the dashboard when exposed on a network.

## Architecture

```
Claude / MCP Client
    |
    v
[MCP Server] -- stdio or HTTP+SSE
    |
    +-- AccountTools, SearchTools, ComposeTools, ...
    |
    +-- [Core]
    |     +-- SQLite (cache.db) + FTS5
    |     +-- CredentialEncryptor
    |     +-- ProviderProfileRegistry
    |
    +-- [ImapClient]
    |     +-- MailKit IMAP/SMTP
    |     +-- SyncManager (IDLE + polling)
    |
    +-- [Queue]
    |     +-- QueueManager (P0/P1/P2 priorities)
    |     +-- Send/Delete/Move/Flag executors
    |
    +-- [LLM]
    |     +-- API (OpenAI/Anthropic) / ACP / InContext
    |     +-- BudgetTracker
    |
    +-- [Dashboard]
          +-- Kestrel + React SPA
          +-- SignalR real-time updates
```

## Documentation

- [Design Specification](docs/superpowers/specs/2026-03-18-ultimate-imap-mcp-design.md) - full system design, architecture, and implementation plan
- [Data Model](docs/DATA_MODEL.md) - SQLite schema and indexes

## License

MIT
