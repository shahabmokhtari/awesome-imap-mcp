-- Newest-first listing per account (tool suggestions, date-ordered searches)
-- without sorting every message in the account.
CREATE INDEX IF NOT EXISTS idx_messages_account_date ON messages(account_id, date_epoch DESC, id DESC);
