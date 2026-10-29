-- Message rows used to be deleted (cache trim, soft-delete purge) without their
-- dependent rows; foreign keys are off, so nothing cascaded. Remove what was left.
DELETE FROM attachments WHERE message_id NOT IN (SELECT id FROM messages);
DELETE FROM llm_analysis WHERE message_id NOT IN (SELECT id FROM messages);
DELETE FROM message_folders WHERE message_id NOT IN (SELECT id FROM messages);
