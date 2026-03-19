CREATE TABLE IF NOT EXISTS sync_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    folder_id       INTEGER REFERENCES folders(id) ON DELETE CASCADE,
    sync_type       TEXT NOT NULL,
    status          TEXT NOT NULL,
    messages_synced INTEGER DEFAULT 0,
    error_message   TEXT,
    started_at      TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at    TEXT,
    duration_ms     INTEGER
);
CREATE INDEX IF NOT EXISTS idx_sync_log_account ON sync_log(account_id, started_at DESC);
