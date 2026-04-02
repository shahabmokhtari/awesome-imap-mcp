---
name: email-management
description: Manage emails — send, reply, forward, delete, move, flag, and organize across IMAP accounts. Use when asked to send, organize, or perform bulk operations.
---

# Email Management

## Compose
- `send_email` — Send new email (to, subject, body, cc, bcc)
- `reply_to` — Reply to a message (replyAll option)
- `forward` — Forward a message

## Organize
- `move_messages` — Move messages to another folder
- `delete_messages` — Delete messages (moves to trash)
- `flag_messages` — Star/unstar messages
- `mark_read` / `mark_unread` — Toggle read status
- `label_messages` — Add/remove labels (IMAP keywords)

## Folders
- `list_folders` — List all folders for an account
- `get_folder_stats` — Folder message counts and stats
- `create_folder` — Create a new IMAP folder

## Attachments
- `list_attachments` — List attachments for a message
- `search_attachments` — Find by filename, type, size, date
- `get_attachment_info` — Detailed attachment info
- `download_attachment` — Download to local filesystem

## Batch Operations
- `fetch_bodies` — Batch-fetch message bodies in one IMAP session
- `detect_duplicates` — Find cross-account duplicate emails
- `delete_duplicates` — Remove duplicates from a specific account

## Account Management
- `list_accounts` / `get_account_status` — View accounts
- `add_account_imap` — Add account via IMAP credentials
- `add_account_oauth` — Start OAuth flow
- `start_dashboard` — Open the dashboard web UI

## Sync & Queue
- `sync_now` — Trigger immediate sync
- `get_sync_status` — Check sync status per folder
- `confirm_send` / `cancel_operation` / `list_pending` — Manage queued operations

## Important Notes
- All write operations go through a queue with undo window
- Send operations support explicit confirm mode
- Delete operations use IMAP STORE \Deleted (server-side)
- Labels use IMAP keywords where supported, local DB fallback otherwise
