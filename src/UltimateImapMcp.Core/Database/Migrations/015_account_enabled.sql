-- Add enabled column to accounts for disabling without deleting.
-- Default 1 (enabled) preserves behavior for all existing accounts.
ALTER TABLE accounts ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
