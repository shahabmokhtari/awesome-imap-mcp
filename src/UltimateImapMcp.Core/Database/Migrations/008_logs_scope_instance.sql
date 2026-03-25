-- Add scope and instance_id columns to logs table for structured filtering
ALTER TABLE logs ADD COLUMN scope TEXT NOT NULL DEFAULT 'system';
ALTER TABLE logs ADD COLUMN instance_id TEXT NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS idx_logs_scope ON logs(scope);
CREATE INDEX IF NOT EXISTS idx_logs_instance ON logs(instance_id);
