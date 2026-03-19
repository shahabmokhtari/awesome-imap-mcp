# Phase 3: Real-Time Sync Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Keep the SQLite cache fresh automatically via IMAP IDLE (real-time) and polling (periodic), with on-demand sync via MCP tools, cache eviction, and sync logging.

**Architecture:** SyncManager BackgroundService manages per-account IDLE listeners and a polling loop. CacheEvictor BackgroundService handles size/time-based eviction. Sync MCP tools (sync_now, get_sync_status) trigger on-demand syncs. ImapConnectionManager upgraded with reconnection + exponential backoff.

**Tech Stack:** .NET 10, MailKit (IDLE), Microsoft.Data.Sqlite, xUnit

**Spec reference:** `docs/superpowers/specs/2026-03-18-ultimate-imap-mcp-design.md` — Sync Manager, Cache Strategy, CacheEvictor sections

---

## File Structure

### New Files
```
src/
  UltimateImapMcp.Core/
    Database/Migrations/
      003_sync_log.sql                    # sync_log table
  UltimateImapMcp.ImapClient/
    SyncManager.cs                        # BackgroundService: IDLE + polling + on-demand
    CacheEvictor.cs                       # BackgroundService: size/time eviction every 10 min
  UltimateImapMcp.McpServer/
    Tools/
      SyncTools.cs                        # sync_now, get_sync_status MCP tools

tests/
  UltimateImapMcp.ImapClient.Tests/
    CacheEvictorTests.cs
```

### Modified Files
```
src/
  UltimateImapMcp.ImapClient/
    ImapConnectionManager.cs              # Add reconnection with exponential backoff
    Repositories/
      MessageRepository.cs               # Add eviction methods
      FolderRepository.cs                # Add sync log methods
  UltimateImapMcp.McpServer/
    Program.cs                            # Register SyncManager + CacheEvictor
```

---

## Chunk 1: Sync Infrastructure

### Task 1: Sync Log Migration + Repository Methods

- Create `src/UltimateImapMcp.Core/Database/Migrations/003_sync_log.sql`
- Add sync log write/read methods to FolderRepository or a new SyncLogRepository

### Task 2: ImapConnectionManager Reconnection

- Upgrade ImapConnectionManager with exponential backoff (1s, 2s, 4s, 8s, max 60s)
- Add health check via NOOP

### Task 3: SyncManager BackgroundService

- IDLE listener per configured folder per account
- Polling loop for non-IDLE folders
- On-demand sync method for MCP tools
- Uses ImapSyncService for actual sync work

### Task 4: CacheEvictor BackgroundService (TDD)

- Runs every 10 minutes
- Size-based: check DB file size vs max_size_mb, evict bodies first, then full rows
- Time-based: if configured, evict by cache_window_days / max_body_age_days
- Tests with in-memory DB

### Task 5: Sync MCP Tools + DI Wiring

- sync_now: trigger immediate sync for a folder or all folders
- get_sync_status: per-folder last sync time, message counts, staleness
- Wire SyncManager + CacheEvictor into Program.cs
