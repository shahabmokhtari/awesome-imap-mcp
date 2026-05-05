-- src/AwesomeImapMcp.Core/Database/Migrations/001_initial.sql
-- Cache tables: folders, messages, FTS, attachments
-- Account config is stored in accounts.json, not in this database.

CREATE TABLE IF NOT EXISTS folders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL,
    path            TEXT NOT NULL,
    display_name    TEXT,
    role            TEXT,
    delimiter       TEXT DEFAULT '/',
    flags           TEXT,
    message_count   INTEGER DEFAULT 0,
    unread_count    INTEGER DEFAULT 0,
    last_synced_uid INTEGER DEFAULT 0,
    last_synced_at  TEXT,
    sync_enabled    INTEGER DEFAULT 1,
    idle_enabled    INTEGER DEFAULT 0,
    poll_interval   INTEGER DEFAULT 300,
    UNIQUE(account_id, path)
);
CREATE INDEX IF NOT EXISTS idx_folders_account ON folders(account_id);
CREATE INDEX IF NOT EXISTS idx_folders_role ON folders(account_id, role);

CREATE TABLE IF NOT EXISTS messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL,
    folder_id       INTEGER NOT NULL,
    uid             INTEGER NOT NULL,
    message_id      TEXT,
    in_reply_to     TEXT,
    references_hdr  TEXT,
    thread_id       TEXT,
    subject         TEXT,
    from_address    TEXT,
    from_email      TEXT,
    to_addresses    TEXT,
    cc_addresses    TEXT,
    bcc_addresses   TEXT,
    date            TEXT NOT NULL,
    date_epoch      INTEGER,
    flags           TEXT,
    size_bytes      INTEGER,
    has_attachments INTEGER DEFAULT 0,
    body_text       TEXT,
    body_html       TEXT,
    body_fetched    INTEGER DEFAULT 0,
    snippet         TEXT,
    raw_headers     TEXT,
    cached_at       TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(account_id, folder_id, uid)
);
CREATE INDEX IF NOT EXISTS idx_messages_account_folder ON messages(account_id, folder_id);
CREATE INDEX IF NOT EXISTS idx_messages_date ON messages(date_epoch DESC);
CREATE INDEX IF NOT EXISTS idx_messages_from ON messages(from_email);
CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);
CREATE INDEX IF NOT EXISTS idx_messages_message_id ON messages(message_id);
CREATE INDEX IF NOT EXISTS idx_messages_flags ON messages(account_id, folder_id, flags);
CREATE INDEX IF NOT EXISTS idx_messages_has_attachments ON messages(has_attachments) WHERE has_attachments = 1;
CREATE INDEX IF NOT EXISTS idx_messages_unread ON messages(account_id, folder_id) WHERE flags NOT LIKE '%\Seen%';

CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
    subject,
    body_text,
    from_address,
    snippet,
    content='messages',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER IF NOT EXISTS messages_fts_insert AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;

CREATE TRIGGER IF NOT EXISTS messages_fts_delete BEFORE DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
END;

CREATE TRIGGER IF NOT EXISTS messages_fts_update AFTER UPDATE OF subject, body_text, from_address, snippet ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;

CREATE TABLE IF NOT EXISTS attachments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      INTEGER NOT NULL,
    filename        TEXT,
    content_type    TEXT,
    size_bytes      INTEGER,
    content_id      TEXT,
    is_inline       INTEGER DEFAULT 0,
    local_path      TEXT,
    downloaded_at   TEXT
);
CREATE INDEX IF NOT EXISTS idx_attachments_message ON attachments(message_id);
