---
name: email-search
description: Search emails with advanced filters — from, to, subject, date, labels, attachments, full-text. Use when asked to find specific emails or filter messages.
---

# Email Search

## Available Tools

- `search_emails` — Advanced search with structured filters
- `list_emails` — Browse emails in a folder (paginated)
- `get_message` — Get full message details + body
- `get_thread` — Get all messages in a conversation thread

## Search Parameters

### Basic
- `query` — Full-text search across subject, body, sender, snippet
- `accountId` — Search within a specific account
- `folder` — Search within a specific folder (e.g., "INBOX")

### Structured Filters
- `from` — Filter by sender email
- `to` — Filter by recipient
- `subject` — Filter by subject (substring)
- `fromDate` / `toDate` — Date range (ISO 8601: "2026-01-01")

### Advanced
- `serverSearch` — Search on IMAP server (slower, searches ALL mail)
- `fetchBodies` — Auto-fetch bodies for all results
- `summaryOnly` — Return only summary fields (default: true)
- `maxBodyLength` — Truncate bodies in results
- `order` — Sort: date_desc, date_asc, from, subject, size_desc

### Dashboard Search Operators
```
from:user@example.com subject:"meeting notes" label:urgent has:attachments before:2026-03-01
```

## Examples

```
search_emails(accountId: "my-gmail", from: "boss@company.com", fromDate: "2026-03-01")
search_emails(query: "invoice", fetchBodies: true, summaryOnly: false)
get_message(accountId: "my-gmail", uid: 12345)
```
