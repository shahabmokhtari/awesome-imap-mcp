-- LLM Analysis tables

CREATE TABLE IF NOT EXISTS llm_analysis (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    analysis_type   TEXT NOT NULL,              -- 'spam_score' | 'category' | 'priority' | 'summary' | 'custom'
    result          TEXT NOT NULL,              -- JSON: { score: 23, label: "newsletter", explanation: "..." }
    model_used      TEXT,                       -- e.g. "claude-haiku-4-5-20251001"
    tokens_input    INTEGER,
    tokens_output   INTEGER,
    cost_usd        REAL,
    analyzed_at     TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(message_id, analysis_type)
);
CREATE INDEX IF NOT EXISTS idx_analysis_message ON llm_analysis(message_id);
CREATE INDEX IF NOT EXISTS idx_analysis_type ON llm_analysis(analysis_type);
CREATE INDEX IF NOT EXISTS idx_analysis_result ON llm_analysis(analysis_type, result);

CREATE TABLE IF NOT EXISTS llm_rules (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT NOT NULL,
    description     TEXT,
    account_id      TEXT REFERENCES accounts(id) ON DELETE CASCADE, -- null = all accounts
    folder_filter   TEXT,                       -- JSON array of folder roles/paths, null = all
    trigger         TEXT NOT NULL,              -- 'on_new' | 'manual' | 'scheduled'
    schedule_cron   TEXT,                       -- cron expression (if trigger = 'scheduled')
    analysis_type   TEXT NOT NULL,              -- 'spam_score' | 'category' | 'custom'
    prompt_template TEXT NOT NULL,              -- LLM prompt with {{subject}}, {{from}}, {{body}} placeholders
    action          TEXT,                       -- JSON: { type: 'label', value: 'spam' } | { type: 'move', folder: 'trash' } | null (analysis only)
    enabled         INTEGER DEFAULT 1,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS llm_usage (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    date            TEXT NOT NULL,              -- ISO date (YYYY-MM-DD)
    model           TEXT NOT NULL,
    tokens_input    INTEGER DEFAULT 0,
    tokens_output   INTEGER DEFAULT 0,
    cost_usd        REAL DEFAULT 0,
    request_count   INTEGER DEFAULT 0,
    UNIQUE(date, model)
);
CREATE INDEX IF NOT EXISTS idx_usage_date ON llm_usage(date);
