# Ultimate IMAP MCP — Design Specification

## Overview

A batteries-included MCP server for email. Works with any IMAP provider (Gmail, Outlook, Fastmail, ProtonMail, Yahoo, self-hosted). Features a local SQLite cache, operation queue with undo, optional web dashboard, LLM-powered email analysis, and full observability.

## Superseded Documents

This spec supersedes the following earlier documents which were written for an initial Node.js/TypeScript design:

- `docs/ARCHITECTURE.md` — superseded by the Architecture, Solution Structure, and component sections in this spec. The .NET stack replaces Node.js, ASP.NET Core replaces Express, SignalR replaces raw WebSocket, MailKit replaces imapflow/nodemailer.
- `docs/PROJECT_PLAN.md` — superseded by the Phased Implementation section in this spec. The tech stack, project structure, and package names are all different.
- `docs/FEATURE_MATRIX.md` — the feature comparison remains valid, but the "Language" row for Ultimate IMAP MCP should read "C# (.NET 8+)" not "TypeScript (Node)".
- `docs/DATA_MODEL.md` — the SQLite schema definitions remain the canonical reference and are valid as-is for the .NET implementation (SQL is stack-agnostic). This spec adds two new tables (`metrics`, `logs`) and one new column (`sends_at` on `operation_queue`).
- `config.example.yaml` — superseded. Config format is JSON (`config.example.json`).

## Stack Decision

### Requirements

- Cross-platform (Windows, Linux, macOS)
- Well-maintained, modern, stable libraries
- High performance (critical requirement)
- React web UI (optional, served by the backend)

### Evaluation

| Requirement | **.NET 8+ (C#)** | **Node.js (TypeScript)** | **Python** |
|---|---|---|---|
| **Performance** | Excellent — JIT compiled, true multithreading, low memory | Good — fast I/O, single-threaded CPU | Weakest — GIL limits concurrency, slower runtime |
| **Cross-platform** | Excellent — .NET 8+ first-class | Excellent | Excellent |
| **IMAP library** | **MailKit 4.15** — best-in-class, full IDLE, by the MIME spec author | imapflow 1.2.9 — solid, actively maintained, IDLE support | aioimaplib — async IDLE, less mature |
| **SMTP library** | MailKit (same library) | nodemailer — battle-tested | smtplib (stdlib) or aiosmtp |
| **SQLite + FTS5** | Microsoft.Data.Sqlite — works with FTS5 via raw SQL | better-sqlite3 — synchronous, fast, native addon | sqlite3 (stdlib) — works, no issues |
| **MCP SDK** | Official C# SDK v1.0 — released March 2026, maintained with Microsoft | Official TS SDK — most mature | Official Python SDK — mature, v2 in development |
| **Background services** | Excellent — `BackgroundService`, `IHostedService`, native async | Workable — event loop, awkward for multiple long-running tasks | asyncio works, GIL hurts parallel CPU work |
| **React dashboard serving** | ASP.NET Core — serves API + static files, WebSocket via SignalR | Express + ws — natural fit | FastAPI + websockets — good but separate ecosystem |
| **Encryption** | `System.Security.Cryptography` — built-in AES-256-GCM | Node `crypto` — built-in | `cryptography` lib — excellent |

### Decision: .NET 8+ (C#)

**Python eliminated** — performance is a critical requirement, and Python's GIL + runtime overhead is the bottleneck for syncing large mailboxes, FTS indexing, and concurrent IMAP connections.

**.NET over Node.js because:**

| Factor | Winner | Why |
|---|---|---|
| Raw performance | .NET | True multithreading, JIT, lower memory footprint |
| IMAP/SMTP library quality | .NET | MailKit is the single best email library in any language |
| Background services (IDLE, queue) | .NET | `BackgroundService` is purpose-built for this |
| MCP SDK maturity | Tie | Both now have official v1.0+ SDKs |

## Architecture

### Monolithic Process (Single Host)

Everything runs in a single .NET process with multiple `BackgroundService` instances:

```
ultimate-imap-mcp.exe
  ├── MCP Server (stdio or HTTP transport)
  ├── SyncManager (BackgroundService — IDLE + polling)
  ├── QueueWorker (BackgroundService — flush cycles)
  ├── MetricsCollector (BackgroundService — aggregation)
  ├── CacheEvictor (BackgroundService — periodic cleanup)
  └── Dashboard (ASP.NET Core Kestrel — optional, enabled via config)
       ├── REST API + SignalR (WebSocket)
       └── React SPA (static files from wwwroot)
```

The dashboard is optional — the MCP server is fully functional standalone with JSON config. All features work without the dashboard.

### Why monolithic

- Single thing to install and run
- Shared DI container — all services inject the same dependencies
- Dashboard is just a config flag: `dashboard_enabled: true/false`
- Clean shutdown via `IHostApplicationLifetime`
- Project boundaries (separate class libraries) make extraction straightforward if ever needed

## Solution Structure

```
ultimate-imap-mcp/
  UltimateImapMcp.sln
  src/
    UltimateImapMcp.Core/              # Shared types, config, DB, encryption, metrics
    UltimateImapMcp.ImapClient/        # IMAP/SMTP abstraction, provider profiles, sync
    UltimateImapMcp.Queue/             # Operation queue, worker, operation executors
    UltimateImapMcp.Llm/               # LLM analysis pipeline (API, ACP, in-context)
    UltimateImapMcp.McpServer/         # MCP tool definitions, server entry point, host
    UltimateImapMcp.Dashboard/         # ASP.NET Core API + SignalR + React SPA
  tests/
    UltimateImapMcp.Core.Tests/
    UltimateImapMcp.ImapClient.Tests/
    UltimateImapMcp.Queue.Tests/
    UltimateImapMcp.Llm.Tests/
    UltimateImapMcp.McpServer.Tests/
    UltimateImapMcp.Dashboard.Tests/
    UltimateImapMcp.IntegrationTests/  # Real IMAP server tests (Dovecot in Docker)
    UltimateImapMcp.E2E.Tests/         # Full MCP flow + Dashboard Playwright tests
  dashboard/
    client/                            # React SPA (Vite + shadcn/ui + TanStack Query + Recharts)
      src/
        pages/
        components/
      package.json
      vite.config.ts
  docs/
  config.example.json
  Dockerfile
  docker-compose.yml                   # For dev: Dovecot test server + the app
```

### Dependency Graph

```
Core ← ImapClient ← Queue ← Llm ← McpServer ← Dashboard
                                         ↑            ↑
                                    (entry point)  (optional)
```

- `Core` depends on nothing (except NuGet packages)
- `ImapClient` depends on `Core`
- `Queue` depends on `Core` + `ImapClient`
- `Llm` depends on `Core` + `ImapClient` (fetches email content for analysis)
- `McpServer` depends on all of the above, hosts the `BackgroundService` instances, is the entry point
- `Dashboard` depends on all of the above, but is conditionally loaded

### Key NuGet Packages

| Package | Used by | Purpose |
|---|---|---|
| MailKit / MimeKit | ImapClient | IMAP, SMTP, MIME parsing |
| Microsoft.Data.Sqlite | Core | SQLite + FTS5 |
| ModelContextProtocol | McpServer | MCP SDK (official C# v1.0) |
| Microsoft.Extensions.AI | Llm | Unified LLM abstraction |
| Microsoft.Extensions.Hosting | McpServer | BackgroundService, DI, config |
| Microsoft.AspNetCore.SignalR | Dashboard | WebSocket for real-time updates |
| System.Diagnostics.DiagnosticSource | Core | Metrics + Activities (OTel) |
| OpenTelemetry.Extensions.Hosting | Core | OTel export (optional) |

## Core Package

### Configuration

JSON-based config loaded via `System.Text.Json`, mapped to strongly-typed C# classes, validated at startup. Environment variable substitution supported (e.g., `${ANTHROPIC_API_KEY}`).

```csharp
public class AppConfig
{
    public ServerConfig Server { get; set; }
    public List<AccountConfig> Accounts { get; set; }
    public CacheConfig Cache { get; set; }
    public QueueConfig Queue { get; set; }
    public LlmConfig Llm { get; set; }
    public MetricsConfig Metrics { get; set; }
}
```

Config resolution order (later overrides earlier):
1. Built-in defaults
2. `config.json` from default location (`~/.ultimate-imap-mcp/config.json`)
3. `--config <path>` CLI argument
4. Environment variables (`UIMAP_SERVER_TRANSPORT=http`, etc.)

### Database

Single SQLite file with WAL mode. Schema managed via numbered migration files applied at startup.

```csharp
public class Database : IDisposable
{
    public SqliteConnection GetReadConnection();   // pooled for concurrent reads
    public SqliteConnection GetWriteConnection();  // single, serialized writes
    public void Migrate();                         // applies pending .sql migrations
}
```

Key decisions:
- **No ORM.** Raw SQL via `Microsoft.Data.Sqlite` for full control over FTS5, triggers, and JSON functions. Thin repository layer per entity for common CRUD.
- **Write serialization.** Single write connection avoids SQLite `SQLITE_BUSY`. Read connections pooled for concurrent dashboard/MCP queries.
- **Migrations** are numbered `.sql` files (`001_initial.sql`, `002_add_metrics.sql`, etc.) applied in order. A `schema_version` table tracks applied migrations.

### Schema

All tables from `DATA_MODEL.md` are the canonical schema reference and remain valid as-is (SQL is stack-agnostic). The following tables are defined there: `accounts`, `folders`, `messages`, `messages_fts` (FTS5 + triggers), `attachments`, `operation_queue`, `llm_analysis`, `llm_rules`, `llm_usage`, `sync_log`, `dashboard_sessions`.

#### Schema modifications to DATA_MODEL.md tables

**operation_queue** — add column for undo window timing:

```sql
ALTER TABLE operation_queue ADD COLUMN sends_at TEXT;  -- ISO 8601 timestamp, set for implicit-confirm sends
```

The `sends_at` column is populated when a send operation is enqueued with implicit confirm mode. The queue worker checks `sends_at <= datetime('now')` during P0 flush to determine if the undo window has expired. For explicit confirm mode, `sends_at` is NULL and the operation waits for `status = 'confirmed'`.

**dashboard_sessions** — extend for PIN/auth storage:

```sql
CREATE TABLE dashboard_auth (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    auth_type   TEXT NOT NULL,           -- 'pin' | 'password'
    username    TEXT,                    -- NULL for PIN mode
    hash        TEXT NOT NULL,           -- bcrypt hash of PIN or password
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
```

The existing `dashboard_sessions` table remains as-is for session token management. `dashboard_auth` stores the PIN hash (PIN mode) or username+password hash (full auth mode).

#### New tables added by this spec

#### metrics

```sql
CREATE TABLE metrics (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    value       REAL NOT NULL,
    tags        TEXT,
    recorded_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_metrics_name_time ON metrics(name, recorded_at DESC);
CREATE INDEX idx_metrics_recorded ON metrics(recorded_at DESC);
```

#### logs

```sql
CREATE TABLE logs (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    level       TEXT NOT NULL,
    category    TEXT NOT NULL,
    message     TEXT NOT NULL,
    exception   TEXT,
    metadata    TEXT,
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_logs_level ON logs(level, created_at DESC);
CREATE INDEX idx_logs_category ON logs(category, created_at DESC);
CREATE INDEX idx_logs_created ON logs(created_at DESC);
```

### Encryption

```csharp
public class CredentialEncryptor
{
    // Key derivation: PBKDF2(passphrase, salt, 100_000 iterations) → 256-bit key
    // passphrase = user-provided OR SHA256(machine-id)
    // Encrypt: AES-256-GCM(plaintext, key, random_nonce) → nonce + ciphertext + tag
    // Stored as base64 in credentials_enc column

    public string Encrypt(string plaintext);
    public string Decrypt(string ciphertext);
}
```

On first run:
1. Check if passphrase provided via CLI arg or env var
2. If not, derive from machine ID and warn: "No passphrase set — credentials are tied to this machine and are not portable. Set `UIMAP_PASSPHRASE` to make them portable."
3. Salt stored alongside the encrypted data

Single code path internally — always passphrase-derived key, only the source of the passphrase varies.

### Metrics & Observability

Built on `System.Diagnostics`:

```csharp
public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new("UltimateImapMcp");
    public static readonly Meter Meter = new("UltimateImapMcp");

    // Histograms: ImapLatency, SmtpLatency, LlmLatency, McpToolLatency
    // Counters: EmailsSynced, OperationsQueued, OperationsCompleted, LlmTokensUsed
    // ObservableGauges: active IMAP connections, queue depth, cache size, email count
}
```

Dual output:
- **Internal:** `MetricsCollector` BackgroundService samples gauges every 30s, writes to `metrics` SQLite table, prunes old entries (default: 7 days retention)
- **External (optional):** if `metrics.otlp_endpoint` configured, export via OTLP to Grafana/Datadog/etc.

## IMAP Client Package

### Connection Management

```csharp
public class ImapConnectionManager
{
    // One persistent connection per account for IDLE
    // Additional connections from a pool for on-demand operations
    // Max connections per account configurable (default: 3)

    public Task<IImapSession> GetSessionAsync(string accountId);
    public Task<IIdleSession> GetIdleSessionAsync(string accountId, string folder);
}

public class SmtpConnectionManager
{
    // Connections created on-demand, pooled, released after send
    public Task SendAsync(string accountId, MimeMessage message);
}
```

Both wrap MailKit with automatic reconnection (exponential backoff: 1s, 2s, 4s, 8s, max 60s), health checks (periodic NOOP), and graceful shutdown via `CancellationToken`.

### Provider Profiles

```csharp
public record ProviderProfile
{
    public string Name { get; init; }
    public Dictionary<FolderRole, string> FolderMap { get; init; }
    public AuthMethod[] SupportedAuth { get; init; }
    public SearchCapabilities Search { get; init; }
    public int MaxConnections { get; init; }
    public bool RequiresTlsTrust { get; init; }
}
```

Auto-detected from IMAP hostname, overridable in config. Profiles for: Gmail, Outlook/O365, Fastmail, ProtonMail Bridge, Yahoo, Generic.

### Sync Manager (BackgroundService)

Three sync strategies running concurrently:

```
SyncManager
  ├── IdleListener (one per IDLE-enabled folder per account)
  │     └── MailKit IDLE → on new message → fetch headers → insert to cache → emit event
  ├── Poller (one loop for all non-IDLE folders)
  │     └── every N seconds → IMAP SEARCH UID {last_synced_uid}:* → fetch new headers → cache
  └── OnDemandSync (triggered by sync_now MCP tool or dashboard)
        └── full or incremental sync of specified folder
```

Incremental sync flow:
1. `SELECT folder` → get `UIDNEXT`
2. `SEARCH UID {last_synced_uid + 1}:*` → get new UIDs
3. `FETCH {uids} (ENVELOPE FLAGS BODYSTRUCTURE RFC822.SIZE)` → headers only
4. Parse headers, extract `Message-ID`, `In-Reply-To`, `References` for threading
5. Generate snippet (first ~200 chars from `BODY.PEEK[TEXT]<0.512>`)
6. Insert into `messages` table, FTS5 triggers fire automatically
7. Update `folders.last_synced_uid`

### Cache Strategy

SQLite is a **cache**, not a full mailbox mirror:

- **Cached (SQLite):** headers + snippets for emails within the cache window. Full bodies fetched lazily, stored with a TTL.
- **Beyond cache (IMAP fallback):** `search_emails` does a two-phase search — hit SQLite first (instant), then IMAP SEARCH for older results if needed. IMAP fallback results are temporarily cached (configurable TTL, default: 1 hour).

**Eviction strategy:**
1. **Size-based (primary):** if DB exceeds `max_size_mb` (default: 500MB), evict oldest entries (by `cached_at`) until under the limit. Bodies are evicted first (set `body_text`, `body_html` to NULL, `body_fetched` to 0), then full message rows if still over limit.
2. **Time-based (optional):** `cache_window_days` and `max_body_age_days` default to `0` (disabled). User can enable if desired.
3. Both can coexist — whichever triggers first wins.

Configured in the setup wizard with sensible defaults. 500MB is safe for SQLite with WAL mode.

**CacheEvictor BackgroundService:** Runs every 10 minutes. Checks DB file size against `max_size_mb`. If over limit, runs eviction in batches of 500 rows to avoid long-running transactions. Also handles time-based eviction if configured. Emits `cache.evictions` counter metric with `reason` tag (size/time).

```json
{
  "cache": {
    "db_path": "~/.ultimate-imap-mcp/cache.db",
    "max_size_mb": 500,
    "default_window_days": 0,
    "max_body_age_days": 0,
    "imap_fallback_ttl_hours": 1
  }
}
```

Per-folder override:

```json
{
  "accounts": [{
    "confirm_mode": "implicit",
    "undo_window_seconds": 10,
    "sync": {
      "folders": [
        { "path": "INBOX", "cache_window_days": 60 },
        { "path": "[Gmail]/Sent Mail", "cache_window_days": 14 }
      ]
    }
  }]
}
```

### Thread Reconstruction

```csharp
public class ThreadBuilder
{
    // thread_id = SHA256(root_message_id) where root = first in References chain
    // On insert, check if thread_id already exists
    // get_thread: SELECT * FROM messages WHERE thread_id = ? ORDER BY date_epoch
}
```

### Message Parsing

MailKit's MimeKit handles all MIME parsing. Extracts:
- Plain text body (prefer `text/plain`, fall back to HTML→text conversion)
- HTML body (stored separately for dashboard rendering)
- Attachment metadata (filename, content type, size, inline flag)
- Address parsing and normalization (`from_email` always lowercase)

## Queue Package

### Queue Manager

```csharp
public class QueueManager
{
    public Task<string> EnqueueAsync(Operation op);
    public Task<bool> CancelAsync(string pendingId);
    public Task<bool> ConfirmAsync(string pendingId);
    public Task<List<QueuedOperation>> ListPendingAsync(string? accountId = null);
    public Task<QueuedOperation?> GetOperationAsync(string pendingId);
}
```

### Priority Tiers & Flush Cycle

`QueueWorker` BackgroundService:

```
every 2s:  flush P0 (send, reply, forward)
every 30s: flush P1 (delete, move, mark, flag, label)
every 5m:  flush P2 (bulk operations)
```

Each flush:
1. SELECT confirmed operations by priority, ordered by created_at
2. For sends: only pick up operations past undo window OR explicitly confirmed
3. Execute via MailKit
4. On success: mark completed
5. On failure: increment retry_count, if < max_retries leave for next cycle, else mark failed

### Send Flow

```
User → LLM calls send_email → QueueManager.Enqueue(P0) → returns pending_id

Account has implicit confirm:
  → response: { pending_id, confirm_mode: "implicit", undo_window_seconds: 10, sends_at: "..." }
  → LLM tells user: "Email queued. It will send in 10 seconds — say 'cancel' to stop it."
  → auto-promotes to 'confirmed' after undo window

Account has explicit confirm:
  → response: { pending_id, confirm_mode: "explicit", status: "awaiting_confirmation" }
  → LLM tells user: "Email queued. Say 'confirm' to send it, or 'cancel' to discard."
  → stays 'pending' until confirm_send called
```

Confirm mode is configurable per account.

### Operation Executors

```csharp
public interface IOperationExecutor
{
    string OperationType { get; }
    Task ExecuteAsync(QueuedOperation op, CancellationToken ct);
}
```

Registered via DI, resolved by `operation` field. One executor per operation type (send, delete, move, flag, bulk, etc.).

## LLM Analysis Package

### Service Abstraction

Three implementations behind a unified interface using `Microsoft.Extensions.AI`:

```csharp
public interface IEmailAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct);
    bool SupportsBackgroundAnalysis { get; }
}
```

| Implementation | Class | How | Background? |
|---|---|---|---|
| API-based | `ApiEmailAnalyzer` | `IChatClient` from Microsoft.Extensions.AI | Yes |
| ACP-based | `AcpEmailAnalyzer` | Spawns Claude Code / Copilot in ACP mode over stdio | Yes |
| In-context | `InContextAnalyzer` | Returns structured email data for calling LLM to analyze | No |

Selected via config:

```json
{
  "llm": {
    "provider": "anthropic",
    "model": "claude-haiku-4-5-20251001",
    "api_key_env": "ANTHROPIC_API_KEY",
    "acp": {
      "command": "claude",
      "args": ["--acp"]
    }
  }
}
```

Provider values: `"anthropic"`, `"openai"`, `"acp_claude"`, `"acp_copilot"`, `"in_context"`

### ACP Client (built-in, lightweight)

No .NET ACP SDK exists, so we build a minimal client that implements enough of the Agent Client Protocol to send prompts and receive responses.

```csharp
public class AcpClient : IAsyncDisposable
{
    // Spawns agent process (e.g., "claude --acp" or "gh copilot --acp") via stdio
    // Communicates using the ACP protocol (JSON-RPC 2.0 over stdin/stdout)

    public Task<AcpSession> CreateSessionAsync(string workingDir);
    public IAsyncEnumerable<AcpEvent> SendPromptAsync(AcpSession session, string prompt);
    public Task CancelAsync(AcpSession session);
    public Task DisposeAsync();  // kills the agent process
}

public record AcpSession(string SessionId, string WorkingDir);

public record AcpEvent
{
    public AcpEventType Type { get; init; }   // TextDelta, ToolUse, PermissionRequest, Complete, Error
    public string? Text { get; init; }         // for TextDelta
    public string? Error { get; init; }        // for Error
}
```

**ACP protocol methods used:**

| JSON-RPC Method | Purpose |
|---|---|
| `initialize` | Handshake: exchange capabilities, protocol version |
| `sessions/create` | Create an isolated session with a working directory |
| `prompts/send` | Send a text prompt, receive streaming events |
| `prompts/cancel` | Cancel an in-flight prompt |
| `sessions/delete` | Clean up a session |

**Lifecycle:** One long-lived agent process per active analysis batch. The process is spawned on first use and kept alive for subsequent analysis requests. Sessions are created per analysis run. The process is killed on `DisposeAsync` or application shutdown.

**Error handling:** If the agent process crashes or becomes unresponsive (no response within 30 seconds), the client kills the process, logs the error, and falls back to the next configured provider (if any). Permission requests from the agent are auto-denied (the analysis client should not need file/tool permissions).

**Protocol reference:** The ACP protocol spec is at https://agentclientprotocol.com. Our implementation covers only the subset needed for prompt→response workflows, not the full protocol (no tool execution, no file operations).

### Analysis Types

- SpamScore (0-100 with explanation)
- Category (newsletter, transactional, personal, work, spam, etc.)
- Priority (low, normal, high, urgent)
- Summary (one-paragraph summary)
- Custom (user-defined prompt template)

### Budget Tracker

Reads/writes `llm_usage` table. Checked before every LLM call. Enforces `daily_token_budget` and `monthly_cost_limit`. Returns clear error when exceeded.

### Rule Engine

Rules defined via dashboard, stored in `llm_rules` table:

- `on_new` → runs automatically when SyncManager emits new-message event
- `manual` → runs when user triggers via dashboard or MCP tool
- `scheduled` → runs on cron schedule (checked by a BackgroundService)

Actions (label, move, archive) are enqueued via the operation queue — reuses the same queue/undo infrastructure.

## MCP Server Package

### Entry Point

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Core
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<CredentialEncryptor>();
builder.Services.AddSingleton<AppConfig>();

// IMAP/SMTP
builder.Services.AddSingleton<ImapConnectionManager>();
builder.Services.AddSingleton<SmtpConnectionManager>();
builder.Services.AddSingleton<ProviderProfileRegistry>();

// Background services
builder.Services.AddHostedService<SyncManager>();
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddHostedService<MetricsCollector>();
builder.Services.AddHostedService<CacheEvictor>();

// LLM
builder.Services.AddEmailAnalyzer(builder.Configuration);

// MCP
builder.Services.AddMcpServer(options => { ... })
    .WithStdioTransport()
    .WithTools<EmailTools>()
    .WithTools<QueueTools>()
    .WithTools<SyncTools>()
    .WithTools<AnalysisTools>()
    .WithTools<ReportTools>()
    .WithTools<AccountTools>();

// Dashboard (conditional)
if (config.Server.DashboardEnabled)
    builder.Services.AddDashboard(config);

await builder.Build().RunAsync();
```

### MCP Tool Definitions

```
EmailTools
  ├── search_emails     → cache-first + IMAP fallback
  ├── get_message       → lazy body fetch
  ├── get_thread        → thread by thread_id
  ├── list_folders      → folder list with stats
  └── get_folder_stats  → detailed folder stats

QueueTools
  ├── send_email        → P0 enqueue
  ├── reply_to          → P0 enqueue
  ├── forward           → P0 enqueue
  ├── delete_messages   → P1 enqueue
  ├── move_messages     → P1 enqueue
  ├── mark_read         → P1 enqueue
  ├── mark_unread       → P1 enqueue
  ├── flag_messages     → P1 enqueue
  ├── label_messages    → P1 enqueue
  ├── confirm_send      → confirm pending
  ├── cancel_operation  → cancel pending
  └── list_pending      → list queue

SyncTools
  ├── sync_now          → trigger sync
  └── get_sync_status   → per-folder status

AnalysisTools
  ├── analyze_email     → single email analysis
  ├── analyze_folder    → batch with budget checks
  └── get_analysis      → read cached results

ReportTools
  ├── mailbox_report    → aggregate stats
  ├── top_senders       → GROUP BY from_email
  └── category_breakdown → aggregate from llm_analysis

AccountTools
  ├── list_accounts     → accounts + connection health
  └── get_account_status → detailed per-account status
```

### Token Budget Awareness

Every tool returning email content supports:

```json
{
  "max_results": 20,
  "summary_only": true,
  "max_body_length": 500
}
```

When `summary_only: true`, only return subject, from, date, snippet — no body.

## Dashboard Package

### Conditionally Loaded

If `dashboard_enabled: false`, the Kestrel host is not started and this assembly is not activated. All MCP features work without the dashboard.

### REST API

```
/api/accounts            GET, POST, PUT, DELETE
/api/accounts/:id/oauth  GET  (start OAuth flow)
/api/accounts/oauth/callback  GET

/api/folders/:accountId  GET
/api/sync/status         GET
/api/sync/trigger        POST

/api/queue               GET  (filterable by status)
/api/queue/:id/cancel    POST
/api/queue/:id/confirm   POST
/api/queue/:id/retry     POST

/api/analysis/run        POST
/api/analysis/results    GET
/api/analysis/rules      GET, POST, PUT, DELETE
/api/analysis/usage      GET

/api/bulk/preview        POST
/api/bulk/execute        POST

/api/reports/volume      GET
/api/reports/senders     GET
/api/reports/categories  GET
/api/reports/spam        GET

/api/metrics             GET
/api/logs                GET

/api/settings            GET, PUT
```

### SignalR Hub

```csharp
public class DashboardHub : Hub
{
    // sync:progress, sync:complete, sync:error
    // queue:added, queue:completed, queue:failed
    // analysis:progress, analysis:complete
    // metrics:update
    // log:new
}
```

Internal services emit events via an `IEventBus` (in-memory pub/sub). The hub subscribes and forwards to connected clients.

```csharp
public interface IEventBus
{
    void Publish<T>(T @event) where T : IEvent;
    IDisposable Subscribe<T>(Action<T> handler) where T : IEvent;
}

// Event types: SyncProgressEvent, SyncCompleteEvent, SyncErrorEvent,
//              QueueAddedEvent, QueueCompletedEvent, QueueFailedEvent,
//              AnalysisProgressEvent, AnalysisCompleteEvent,
//              MetricsUpdateEvent, LogEvent
```

The `IEventBus` is registered as a singleton in the DI container. SyncManager, QueueWorker, MetricsCollector, and the LLM rule engine publish events. The SignalR hub and any other interested services subscribe. Implementation is a simple in-memory `Channel<T>`-based pub/sub — no external message broker needed.

### React SPA

**Stack:** Vite + React + TypeScript + shadcn/ui + TanStack Query + Recharts

| Page | Purpose |
|---|---|
| Overview | Health cards, recent errors, metrics sparklines |
| Accounts | CRUD, OAuth flows, test connection, setup wizard |
| Sync | Per-folder sync status, manual sync |
| Queue | Operation list, cancel/confirm/retry |
| Analysis | Results table, rule builder, token usage charts |
| Bulk Ops | Query builder, preview, execute, progress, undo |
| Reports | Volume charts, top senders, category pie, spam histogram, folder sizes |
| Metrics | Latency charts, counters, gauges for IMAP/SMTP/LLM/MCP/cache |
| Logs | Searchable/filterable log viewer, live tail via SignalR |
| Settings | Config editor, passphrase management, auth settings |

### Authentication

- **Default (PIN mode):** On first access, user sets a PIN. Stored as bcrypt hash in `dashboard_auth` table. Session token stored in `dashboard_sessions` with configurable expiry (default: 24 hours).
- **Full auth (optional):** Username + password login, stored in `dashboard_auth` with `auth_type = 'password'`. Enabled via config: `"dashboard_auth": "full"`. For when dashboard is exposed beyond localhost.

### OAuth2 Flows

OAuth2 for Gmail and Outlook is handled entirely through the dashboard:

1. **Client credentials:** User provides their own OAuth `client_id` and `client_secret` (from Google Cloud Console or Azure AD app registration) via the dashboard Accounts page. These are encrypted and stored in the `accounts.credentials_enc` field.
2. **Authorization:** Dashboard redirects the browser to the provider's auth URL with `redirect_uri=http://localhost:{dashboard_port}/api/accounts/oauth/callback` (loopback redirect, no external server needed).
3. **Token exchange:** Callback endpoint receives the auth code, exchanges it for access + refresh tokens via the provider's token endpoint, encrypts and stores them.
4. **Token refresh:** `ImapConnectionManager` checks token expiry before each IMAP/SMTP connection. If expired, refreshes using the stored refresh token. If refresh fails, marks the account as requiring re-auth and emits an event to the dashboard.

This uses the standard OAuth2 loopback redirect flow (RFC 8252 Section 7.3), which is the recommended approach for native/localhost apps.

### Build Integration

- `dashboard/client/` has its own `package.json`
- MSBuild target in `UltimateImapMcp.Dashboard.csproj` runs `npm run build` and copies output to `wwwroot/`
- In dev: Vite dev server with proxy to ASP.NET Core API (hot reload)

## Observability

### Metrics Collected

| Category | Metric | Type | Tags |
|---|---|---|---|
| IMAP | `imap.connect_ms` | Histogram | account, host |
| | `imap.command_ms` | Histogram | account, command |
| | `imap.active_connections` | Gauge | account |
| | `imap.reconnects` | Counter | account |
| SMTP | `smtp.send_ms` | Histogram | account |
| | `smtp.sends_total` | Counter | account, status |
| Sync | `sync.duration_ms` | Histogram | account, folder, type |
| | `sync.messages_synced` | Counter | account, folder |
| | `sync.errors` | Counter | account, folder |
| Queue | `queue.depth` | Gauge | priority |
| | `queue.enqueued` | Counter | operation_type |
| | `queue.completed` | Counter | operation_type |
| | `queue.failed` | Counter | operation_type |
| | `queue.flush_ms` | Histogram | priority |
| | `queue.wait_time_ms` | Histogram | priority |
| LLM | `llm.request_ms` | Histogram | provider, model, analysis_type |
| | `llm.tokens_input` | Counter | provider, model |
| | `llm.tokens_output` | Counter | provider, model |
| | `llm.cost_usd` | Counter | provider, model |
| | `llm.errors` | Counter | provider, error_type |
| MCP | `mcp.tool_call_ms` | Histogram | tool_name |
| | `mcp.tool_calls` | Counter | tool_name |
| | `mcp.tool_errors` | Counter | tool_name |
| Cache | `cache.size_bytes` | Gauge | |
| | `cache.messages_count` | Gauge | account |
| | `cache.hit_rate` | Gauge | |
| | `cache.evictions` | Counter | reason |
| Dashboard | `dashboard.active_sessions` | Gauge | |
| | `dashboard.api_request_ms` | Histogram | endpoint |

### Logging

Built on `Microsoft.Extensions.Logging` with a custom `SqliteLoggerProvider`:
- Buffers log entries, batch-writes to `logs` table every 5 seconds
- Configurable minimum level per category
- Auto-prunes: debug 7 days, info 30 days, warn/error 90 days
- Dashboard log viewer: live tail via SignalR, historical search via SQLite

### Tracing

OpenTelemetry `Activity` spans for key operations:

```
mcp.tool_call (root span)
  └── cache.search
      └── imap.search (if fallback)
          └── imap.fetch

queue.flush
  └── queue.execute_operation
      └── smtp.send / imap.store / imap.copy
```

Only exported if OTel is configured. Zero overhead otherwise.

### Configuration

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

## Containerization & Distribution

### Running Modes

1. **Direct:** `dotnet run` / published binary
2. **Global tool:** `dotnet tool install -g ultimate-imap-mcp && ultimate-imap-mcp`
3. **Container:** `docker run -v ~/.ultimate-imap-mcp:/data ultimate-imap-mcp`
4. **MCP client:** `claude mcp add ultimate-imap-mcp -- ultimate-imap-mcp`

### Dockerfile

Multi-stage build:
- Stage 1: Node — build React SPA (`npm ci && npm run build`)
- Stage 2: .NET SDK — `dotnet publish -c Release --self-contained`
- Stage 3: Runtime (slim) — copy published output + wwwroot, multi-arch (amd64 + arm64)

### docker-compose.yml (development)

Includes the app + Dovecot (local IMAP server for integration tests).

### Claude Code Plugin

`SKILL.md` ships alongside the MCP server with tool usage guidelines (e.g., use `summary_only` first, check `confirm_mode` in send responses).

## Testing Strategy

- **Unit tests (xUnit):** all modules — config parsing, encryption, queue logic, thread reconstruction, provider profiles, cache eviction, budget tracker
- **Integration tests:** real IMAP server (Dovecot in Docker) — connection, sync, search, send, IDLE
- **E2E tests:** full MCP server — send tool calls, verify responses. Dashboard Playwright tests.

## Phased Implementation

### Phase 1: Foundation (Core + IMAP Read)

- Solution scaffold
- Core: config (JSON), SQLite + migrations, encryption, types, provider profiles
- ImapClient: MailKit connection manager, folder mapper, message parser, header-only sync
- McpServer: host, MCP SDK (stdio), read tools
- Cache: SQLite + FTS5, size-based eviction
- Tests + config.example.json

**Deliverable:** Search/read emails from any IMAP provider via MCP.

### Phase 2: Write Operations + Queue

- Queue: SQLite-backed queue, priority tiers, flush worker, operation executors
- SMTP via MailKit
- Write MCP tools + queue management tools
- Configurable confirm mode per account
- Dead-letter, retry with backoff

**Deliverable:** Full read/write email via MCP with safety rails.

### Phase 3: Real-Time Sync

- SyncManager BackgroundService: IDLE, polling, on-demand
- Reconnection with exponential backoff
- Sync tools, sync log, cache evictor
- IMAP fallback for older searches

**Deliverable:** Cache stays fresh, INBOX near real-time.

### Phase 4: Dashboard

- ASP.NET Core + SignalR + static files
- React SPA scaffold
- Pages: Overview, Accounts (setup wizard), Sync, Queue, Settings
- Auth: PIN (default) + full auth (optional)
- OAuth2 flows for Gmail/Outlook

**Deliverable:** Manage and monitor via browser.

### Phase 5: LLM Analysis

- Microsoft.Extensions.AI (API-based), ACP client (Claude/Copilot), in-context
- Analysis types, budget tracker, rule engine
- Analysis MCP tools
- Dashboard: Analysis page, rule builder, usage charts

**Deliverable:** LLM-scored email intelligence.

### Phase 6: Bulk Ops + Reports + Observability

- Bulk Ops page (query, preview, execute, undo)
- Reports page (charts, tables)
- Report MCP tools
- Metrics infrastructure (System.Diagnostics + SQLite + OTel)
- Dashboard: Metrics + Logs pages
- SQLite log sink

**Deliverable:** Full mailbox intelligence, observability.

### Phase 7: Polish + Distribution

- .NET global tool, Docker image
- HTTP+SSE transport for MCP (alternative to stdio, for remote MCP clients; uses the same Kestrel host as the dashboard on a separate port `http_port`, configured via `"transport": "http"` or `"transport": "both"` in config)
- Claude Code plugin bundle
- CI/CD (GitHub Actions)
- Security audit, performance testing (100k+ messages)
- Documentation
