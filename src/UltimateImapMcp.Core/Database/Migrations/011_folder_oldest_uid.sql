ALTER TABLE folders ADD COLUMN oldest_synced_uid INTEGER DEFAULT 0;
-- Initialize oldest_synced_uid to last_synced_uid for existing folders
-- (so backfill starts from where forward sync left off)
UPDATE folders SET oldest_synced_uid = last_synced_uid WHERE last_synced_uid > 0;
