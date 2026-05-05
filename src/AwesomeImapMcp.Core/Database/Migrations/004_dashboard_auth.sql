-- Dashboard sessions table (ephemeral — survives in cache DB, lost on cache clear)
-- PIN auth is stored in config file, not DB.

CREATE TABLE IF NOT EXISTS dashboard_sessions (
    token           TEXT PRIMARY KEY,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL
);
