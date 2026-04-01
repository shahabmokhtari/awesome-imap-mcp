-- Accounts moved to accounts.json. Only folder cursor remains.
-- Add sync_cursor column to folders for REST backends that use cursor-based pagination.
ALTER TABLE folders ADD COLUMN sync_cursor TEXT;
