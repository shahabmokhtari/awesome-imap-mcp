# awesome-imap-mcp

## Tool Usage Guide

### Search Strategy
- Always use `search_emails` with `summary_only: true` first to get an overview
- Then use `get_message` for specific emails you want to read in full
- Use `max_body_length` to limit response size for long emails

### Send Strategy
- Check the `confirm_mode` in the `send_email` response
- If `implicit`: tell the user the email will send in N seconds, they can say "cancel"
- If `explicit`: tell the user to say "confirm" to send, or "cancel" to discard
- Always use `list_pending` before suggesting new operations to avoid duplicates

### Analysis
- Use `analyze_email` for individual emails
- Use `analyze_folder` for batch analysis (respects token budget)
- Check `get_analysis_budget` before large analysis runs

### Sync
- Use `sync_now` if you need fresh data
- Use `get_sync_status` to check cache freshness
