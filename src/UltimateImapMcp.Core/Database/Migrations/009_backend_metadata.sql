-- Add backend_type column to accounts for routing to the correct email backend.
-- Default 'imap' preserves behavior for all existing accounts.
ALTER TABLE accounts ADD COLUMN backend_type TEXT NOT NULL DEFAULT 'imap';

-- Add sync_cursor column to folders for REST backends that use cursor-based pagination.
ALTER TABLE folders ADD COLUMN sync_cursor TEXT;
