CREATE TABLE IF NOT EXISTS operation_queue (
    id              TEXT PRIMARY KEY,
    account_id      TEXT NOT NULL,
    operation       TEXT NOT NULL,
    priority        INTEGER NOT NULL DEFAULT 1,
    status          TEXT NOT NULL DEFAULT 'pending',
    payload         TEXT NOT NULL,
    requires_confirm INTEGER DEFAULT 0,
    error_message   TEXT,
    retry_count     INTEGER DEFAULT 0,
    max_retries     INTEGER DEFAULT 3,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    confirmed_at    TEXT,
    started_at      TEXT,
    completed_at    TEXT,
    cancelled_at    TEXT,
    sends_at        TEXT
);
CREATE INDEX IF NOT EXISTS idx_queue_status ON operation_queue(status, priority, created_at);
CREATE INDEX IF NOT EXISTS idx_queue_account ON operation_queue(account_id, status);
