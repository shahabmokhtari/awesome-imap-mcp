CREATE TABLE IF NOT EXISTS oauth_tokens (
    account_id        TEXT PRIMARY KEY,
    provider          TEXT NOT NULL,
    client_id         TEXT NOT NULL,
    client_secret_enc TEXT,
    refresh_token_enc TEXT NOT NULL,
    access_token_enc  TEXT,
    token_expiry      TEXT,
    scopes            TEXT,
    email             TEXT,
    created_at        TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at        TEXT NOT NULL DEFAULT (datetime('now'))
);
