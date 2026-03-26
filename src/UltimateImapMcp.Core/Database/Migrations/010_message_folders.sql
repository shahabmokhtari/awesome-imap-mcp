-- Junction table mapping messages to folders (many-to-many)
-- A message can appear in multiple folders, but is stored once in the messages table.
CREATE TABLE IF NOT EXISTS message_folders (
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    uid INTEGER NOT NULL,
    PRIMARY KEY (message_id, folder_id)
);

-- Index for folder-based queries (list messages in folder)
CREATE INDEX IF NOT EXISTS idx_message_folders_folder ON message_folders(folder_id, uid DESC);

-- Index for UID-based lookups (find message by folder + uid)
CREATE UNIQUE INDEX IF NOT EXISTS idx_message_folders_folder_uid ON message_folders(folder_id, uid);

-- Populate junction table from existing messages data
INSERT OR IGNORE INTO message_folders (message_id, folder_id, uid)
SELECT id, folder_id, uid FROM messages;
