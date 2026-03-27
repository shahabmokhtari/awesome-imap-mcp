-- Soft-delete support: messages deleted on server are marked with deleted_at
-- instead of immediately removed. They're purged after a configurable retention period.
ALTER TABLE messages ADD COLUMN deleted_at TEXT;
CREATE INDEX IF NOT EXISTS idx_messages_deleted ON messages(deleted_at) WHERE deleted_at IS NOT NULL;
