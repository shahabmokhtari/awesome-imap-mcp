# Data Model (SQLite)

All data lives in a single SQLite database with WAL mode enabled for concurrent read/write.

## Schema

### accounts

Stores configured email accounts. Credentials stored AES-256-GCM encrypted.

```sql
CREATE TABLE accounts (
    id              TEXT PRIMARY KEY,           -- slugified name, e.g. "personal"
    name            TEXT NOT NULL,              -- display name
    imap_host       TEXT NOT NULL,
    imap_port       INTEGER NOT NULL DEFAULT 993,
    smtp_host       TEXT,
    smtp_port       INTEGER DEFAULT 465,
    smtp_use_ssl    INTEGER DEFAULT 1,
    username        TEXT NOT NULL,
    auth_type       TEXT NOT NULL,              -- 'oauth2' | 'password' | 'app_password'
    credentials_enc TEXT NOT NULL,              -- encrypted JSON blob (password/tokens)
    provider        TEXT NOT NULL DEFAULT 'generic', -- 'gmail' | 'outlook' | 'fastmail' | 'protonmail' | 'yahoo' | 'generic'
    config_json     TEXT,                       -- provider-specific config overrides (JSON)
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
```

### folders

Tracks IMAP folders and sync state per account.

```sql
CREATE TABLE folders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    path            TEXT NOT NULL,              -- IMAP folder path, e.g. "[Gmail]/Sent Mail"
    display_name    TEXT,                       -- friendly name, e.g. "Sent"
    role            TEXT,                       -- 'inbox' | 'sent' | 'drafts' | 'trash' | 'spam' | 'archive' | null
    delimiter       TEXT DEFAULT '/',
    flags           TEXT,                       -- JSON array of IMAP folder flags
    message_count   INTEGER DEFAULT 0,
    unread_count    INTEGER DEFAULT 0,
    last_synced_uid INTEGER DEFAULT 0,         -- highest UID synced (for incremental)
    last_synced_at  TEXT,
    sync_enabled    INTEGER DEFAULT 1,
    idle_enabled    INTEGER DEFAULT 0,         -- use IMAP IDLE for this folder
    poll_interval   INTEGER DEFAULT 300,       -- seconds between polls
    UNIQUE(account_id, path)
);
CREATE INDEX idx_folders_account ON folders(account_id);
CREATE INDEX idx_folders_role ON folders(account_id, role);
```

### messages

Core message cache. Headers always synced, body lazy-fetched.

```sql
CREATE TABLE messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    folder_id       INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    uid             INTEGER NOT NULL,           -- IMAP UID within the folder
    message_id      TEXT,                       -- RFC 2822 Message-ID header
    in_reply_to     TEXT,                       -- In-Reply-To header (for threading)
    references_hdr  TEXT,                       -- References header (for threading, space-separated)
    thread_id       TEXT,                       -- computed thread ID (hash of root message_id)
    subject         TEXT,
    from_address    TEXT,                       -- "Name <email>" format
    from_email      TEXT,                       -- just the email, normalized lowercase
    to_addresses    TEXT,                       -- JSON array of "Name <email>"
    cc_addresses    TEXT,                       -- JSON array
    bcc_addresses   TEXT,                       -- JSON array
    date            TEXT NOT NULL,              -- RFC 2822 date, stored as ISO 8601
    date_epoch      INTEGER,                   -- unix timestamp for fast range queries
    flags           TEXT,                       -- JSON array: ["\\Seen", "\\Flagged", ...]
    size_bytes      INTEGER,
    has_attachments INTEGER DEFAULT 0,
    body_text       TEXT,                       -- plain text body (null if not yet fetched)
    body_html       TEXT,                       -- HTML body (null if not yet fetched)
    body_fetched    INTEGER DEFAULT 0,         -- 0 = headers only, 1 = full body cached
    snippet         TEXT,                       -- first ~200 chars of body (always populated)
    raw_headers     TEXT,                       -- full raw headers (for debugging/advanced use)
    cached_at       TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(account_id, folder_id, uid)
);

CREATE INDEX idx_messages_account_folder ON messages(account_id, folder_id);
CREATE INDEX idx_messages_date ON messages(date_epoch DESC);
CREATE INDEX idx_messages_from ON messages(from_email);
CREATE INDEX idx_messages_thread ON messages(thread_id);
CREATE INDEX idx_messages_message_id ON messages(message_id);
CREATE INDEX idx_messages_flags ON messages(account_id, folder_id, flags);
CREATE INDEX idx_messages_has_attachments ON messages(has_attachments) WHERE has_attachments = 1;
CREATE INDEX idx_messages_unread ON messages(account_id, folder_id) WHERE flags NOT LIKE '%\\Seen%';
```

### messages_fts (Full-Text Search)

FTS5 virtual table for fast text search over subject and body.

```sql
CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject,
    body_text,
    from_address,
    snippet,
    content='messages',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

-- Triggers to keep FTS in sync
CREATE TRIGGER messages_fts_insert AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;

CREATE TRIGGER messages_fts_delete BEFORE DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
END;

CREATE TRIGGER messages_fts_update AFTER UPDATE OF subject, body_text, from_address, snippet ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;
```

### attachments

Metadata about message attachments. Actual files stored in local temp directory.

```sql
CREATE TABLE attachments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    filename        TEXT,
    content_type    TEXT,
    size_bytes      INTEGER,
    content_id      TEXT,                       -- for inline attachments
    is_inline       INTEGER DEFAULT 0,
    local_path      TEXT,                       -- path to downloaded file (null if not downloaded)
    downloaded_at   TEXT
);
CREATE INDEX idx_attachments_message ON attachments(message_id);
```

### operation_queue

Queue for all write operations. Processed by the queue worker.

```sql
CREATE TABLE operation_queue (
    id              TEXT PRIMARY KEY,           -- UUID
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    operation       TEXT NOT NULL,              -- 'send' | 'reply' | 'forward' | 'delete' | 'move' | 'mark_read' | 'mark_unread' | 'flag' | 'unflag' | 'label' | 'unlabel' | 'bulk_delete' | 'bulk_move'
    priority        INTEGER NOT NULL DEFAULT 1, -- 0 = near-immediate, 1 = batched, 2 = background
    status          TEXT NOT NULL DEFAULT 'pending', -- 'pending' | 'confirmed' | 'processing' | 'completed' | 'failed' | 'cancelled'
    payload         TEXT NOT NULL,              -- JSON: operation-specific data
    requires_confirm INTEGER DEFAULT 0,         -- 1 = needs explicit confirm (sends)
    error_message   TEXT,
    retry_count     INTEGER DEFAULT 0,
    max_retries     INTEGER DEFAULT 3,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    confirmed_at    TEXT,
    started_at      TEXT,
    completed_at    TEXT,
    cancelled_at    TEXT
);
CREATE INDEX idx_queue_status ON operation_queue(status, priority, created_at);
CREATE INDEX idx_queue_account ON operation_queue(account_id, status);
```

### llm_analysis

Results of LLM analysis on individual messages.

```sql
CREATE TABLE llm_analysis (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    analysis_type   TEXT NOT NULL,              -- 'spam_score' | 'category' | 'priority' | 'summary' | 'custom'
    result          TEXT NOT NULL,              -- JSON: { score: 23, label: "newsletter", explanation: "..." }
    model_used      TEXT,                       -- e.g. "claude-haiku-4-5-20251001"
    tokens_input    INTEGER,
    tokens_output   INTEGER,
    cost_usd        REAL,
    analyzed_at     TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(message_id, analysis_type)
);
CREATE INDEX idx_analysis_message ON llm_analysis(message_id);
CREATE INDEX idx_analysis_type ON llm_analysis(analysis_type);
CREATE INDEX idx_analysis_result ON llm_analysis(analysis_type, result);
```

### llm_rules

User-defined LLM analysis rules (managed via dashboard).

```sql
CREATE TABLE llm_rules (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT NOT NULL,
    description     TEXT,
    account_id      TEXT REFERENCES accounts(id) ON DELETE CASCADE, -- null = all accounts
    folder_filter   TEXT,                       -- JSON array of folder roles/paths, null = all
    trigger         TEXT NOT NULL,              -- 'on_new' | 'manual' | 'scheduled'
    schedule_cron   TEXT,                       -- cron expression (if trigger = 'scheduled')
    analysis_type   TEXT NOT NULL,              -- 'spam_score' | 'category' | 'custom'
    prompt_template TEXT NOT NULL,              -- LLM prompt with {{subject}}, {{from}}, {{body}} placeholders
    action          TEXT,                       -- JSON: { type: 'label', value: 'spam' } | { type: 'move', folder: 'trash' } | null (analysis only)
    enabled         INTEGER DEFAULT 1,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);
```

### llm_usage

Tracks LLM API usage for budgeting.

```sql
CREATE TABLE llm_usage (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    date            TEXT NOT NULL,              -- ISO date (YYYY-MM-DD)
    model           TEXT NOT NULL,
    tokens_input    INTEGER DEFAULT 0,
    tokens_output   INTEGER DEFAULT 0,
    cost_usd        REAL DEFAULT 0,
    request_count   INTEGER DEFAULT 0,
    UNIQUE(date, model)
);
CREATE INDEX idx_usage_date ON llm_usage(date);
```

### sync_log

Tracks sync history for monitoring and debugging.

```sql
CREATE TABLE sync_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    folder_id       INTEGER REFERENCES folders(id) ON DELETE CASCADE,
    sync_type       TEXT NOT NULL,              -- 'idle' | 'poll' | 'manual' | 'full'
    status          TEXT NOT NULL,              -- 'started' | 'completed' | 'failed'
    messages_synced INTEGER DEFAULT 0,
    error_message   TEXT,
    started_at      TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at    TEXT,
    duration_ms     INTEGER
);
CREATE INDEX idx_sync_log_account ON sync_log(account_id, started_at DESC);
```

### dashboard_sessions

Simple session store for dashboard auth (optional, for multi-user setups).

```sql
CREATE TABLE dashboard_sessions (
    token           TEXT PRIMARY KEY,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL
);
```

## Notes

- All timestamps stored as ISO 8601 strings in UTC
- JSON fields use SQLite's built-in json functions for querying (e.g., `json_extract(flags, '$')`)
- WAL mode enabled at database creation: `PRAGMA journal_mode=WAL;`
- Foreign keys enabled: `PRAGMA foreign_keys=ON;`
- Database file default location: `~/.ultimate-imap-mcp/cache.db`
- Encryption key for credentials derived from machine ID + user-provided passphrase (optional)
