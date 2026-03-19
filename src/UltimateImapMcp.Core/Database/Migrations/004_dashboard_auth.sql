-- Dashboard authentication and sessions tables

CREATE TABLE IF NOT EXISTS dashboard_auth (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    auth_type   TEXT NOT NULL,           -- 'pin' | 'password'
    username    TEXT,                    -- NULL for PIN mode
    hash        TEXT NOT NULL,           -- bcrypt hash of PIN or password
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS dashboard_sessions (
    token           TEXT PRIMARY KEY,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at      TEXT NOT NULL
);
