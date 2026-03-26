-- Track whether a message was fetched by sync or on-demand (server search, fetch-body).
-- Sync boundaries (GetMaxUid/GetMinUid) should only consider synced messages.
ALTER TABLE messages ADD COLUMN synced INTEGER NOT NULL DEFAULT 1;
