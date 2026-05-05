# REST Email Backends

This document explains how the modular email backend system works and how to add support for a new REST-based email provider.

## Architecture Overview

The server uses two interface pairs to abstract email operations:

```
IEmailSyncBackend    — read/sync side (folders, messages, body fetch)
IEmailOperationBackend — write side (send, move, delete, flag)
```

An `IEmailBackendFactory` routes each account to the correct backend implementation based on the `backend_type` column in the `accounts` table.

```
┌─────────────────────────────────────────────┐
│              MCP Tools / Queue              │
└──────────────────┬──────────────────────────┘
                   │
        ┌──────────▼──────────┐
        │ IEmailBackendFactory │
        │ (CompositeBackend)   │
        └──────┬────────┬─────┘
               │        │
    ┌──────────▼─┐  ┌───▼──────────┐
    │  IMAP      │  │  Zoho REST   │
    │  Backend   │  │  Backend     │
    └──────┬─────┘  └───┬──────────┘
           │            │
    ┌──────▼─────┐  ┌───▼──────────┐
    │  MailKit   │  │  HttpClient  │
    │  (IMAP/    │  │  (REST API)  │
    │   SMTP)    │  │              │
    └────────────┘  └──────────────┘
```

All backends write to the same local SQLite cache via shared repositories (`FolderRepository`, `MessageRepository`, etc.). MCP tools and the dashboard read from SQLite — they don't know which backend populated the data.

## Interface Reference

### IEmailSyncBackend

```csharp
public interface IEmailSyncBackend : IAsyncDisposable
{
    string BackendType { get; }
    Task SyncFoldersAsync(string accountId, CancellationToken ct = default);
    Task SyncFolderMessagesAsync(string accountId, string folderPath, CancellationToken ct = default);
    Task FetchMessageBodyAsync(string accountId, string folderPath, long uid, CancellationToken ct = default);
    bool SupportsRealtimeSync { get; }
    Task StartRealtimeListenerAsync(string accountId, string folderPath,
        Func<Task> onChangesDetected, CancellationToken ct = default);
}
```

Key design points:
- `SyncFoldersAsync` discovers folders and upserts them into `FolderRepository`.
- `SyncFolderMessagesAsync` fetches message metadata and stores it in `MessageRepository`.
- `FetchMessageBodyAsync` fetches the full body for a single message on demand.
- `SupportsRealtimeSync` — return `true` if your API supports push/webhook, `false` for polling-only.

### IEmailOperationBackend

```csharp
public interface IEmailOperationBackend : IAsyncDisposable
{
    string BackendType { get; }
    Task SendAsync(string accountId, EmailMessage message, CancellationToken ct = default);
    Task MoveAsync(string accountId, IReadOnlyList<long> uids, string fromFolder,
        string toFolder, CancellationToken ct = default);
    Task DeleteAsync(string accountId, IReadOnlyList<long> uids, string folder,
        CancellationToken ct = default);
    Task SetFlagsAsync(string accountId, IReadOnlyList<long> uids, string folder,
        MessageAction action, CancellationToken ct = default);
}
```

## Step-by-Step: Creating a New Backend

### 1. Create Your API Client

In `src/AwesomeImapMcp.RestBackend/YourProvider/`:

```csharp
// YourProviderApiClient.cs — low-level HTTP wrapper
internal sealed class YourProviderApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthAccessTokenProvider _tokenProvider;

    // Methods: GetFolders, GetMessages, GetMessageDetail, Send, Move, Delete, UpdateFlags
}
```

### 2. Create Model DTOs

```csharp
// YourProviderModels.cs — DTOs for JSON deserialization
internal sealed class YourProviderFolder { ... }
internal sealed class YourProviderMessage { ... }
```

### 3. Implement IEmailSyncBackend

```csharp
internal sealed class YourProviderSyncBackend : IEmailSyncBackend
{
    public string BackendType => "yourprovider_rest";
    public bool SupportsRealtimeSync => false;

    public async Task SyncFoldersAsync(string accountId, CancellationToken ct)
    {
        // 1. Call your API to list folders
        // 2. For each folder, call _folderRepo.Insert(...)
        // 3. Update message/unread counts via _folderRepo.UpdateSyncState(...)
    }

    public async Task SyncFolderMessagesAsync(string accountId, string folderPath, CancellationToken ct)
    {
        // 1. Look up the folder in DB
        // 2. Page through messages from the API
        // 3. For each message, call _messageRepo.Insert(...)
        // 4. Update sync state
    }
}
```

### 4. Implement IEmailOperationBackend

```csharp
internal sealed class YourProviderOperationBackend : IEmailOperationBackend
{
    // Delegate each operation to your API client
}
```

### 5. Create a Background Sync Service (if polling-only)

```csharp
public sealed class YourProviderSyncService : BackgroundService
{
    // Poll all accounts with backend_type == "yourprovider_rest"
    // every N minutes
}
```

### 6. Register in CompositeBackendFactory

In `CompositeBackendFactory.cs`, add cases to `CreateSyncBackend` and `CreateOperationBackend`:

```csharp
return backendType switch
{
    "zoho_rest" => CreateZohoSyncBackend(),
    "yourprovider_rest" => CreateYourProviderSyncBackend(),  // ADD THIS
    _ => CreateImapSyncBackend(accountId)
};
```

### 7. Register in DI (Program.cs)

```csharp
builder.Services.AddHttpClient("YourProvider");
builder.Services.AddHostedService<YourProviderSyncService>();
```

### 8. Add OAuth Defaults (if applicable)

In `OAuthProviderDefaults.cs`:

```csharp
["yourprovider"] = new OAuthProviderConfig
{
    ClientId = "CONFIGURE_ME",
    AuthUrl = "https://yourprovider.com/oauth/authorize",
    TokenUrl = "https://yourprovider.com/oauth/token",
    Scopes = ["mail.read", "mail.send"]
},
```

### 9. Add a Database Migration

Create `NNN_yourprovider.sql` if you need additional columns. The `backend_type` column on `accounts` and `sync_cursor` on `folders` already exist.

## How Sync Works

### Polling-Based (REST Backends)

REST backends typically use polling because most email APIs lack push/webhook support:

1. A `BackgroundService` runs a timer loop (default: 5 minutes)
2. On each tick, it queries `AccountRepository.GetAll()` and filters by `backend_type`
3. For each matching account, it creates a sync backend via `IEmailBackendFactory`
4. Calls `SyncFoldersAsync` then `SyncFolderMessagesAsync` for each enabled folder
5. The sync backend pages through API results and inserts into SQLite

### Cursor-Based Pagination

The `folders` table has a `sync_cursor` column for REST backends to track their position:

```csharp
// Save where we left off
_folderRepo.UpdateSyncCursor(folderId, nextPageToken);

// Resume from cursor on next sync
var cursor = dbFolder.SyncCursor;
```

### IMAP Sync (Existing)

The IMAP backend uses UID-based incremental sync and IDLE for real-time. The `SyncManager` handles this directly — it has not been refactored to use `IEmailSyncBackend` yet.

## How Operations Work

Operations (send, move, delete, flag) can be invoked two ways:

1. **Directly** via `IEmailOperationBackend` — for immediate execution
2. **Via the queue** — `QueueManager` enqueues the operation, `QueueWorker` dequeues and executes

Currently, queue executors use IMAP directly. Future refactoring will route them through `IEmailBackendFactory`.

### Send

```csharp
await backend.SendAsync(accountId, new EmailMessage(
    To: "user@example.com",
    Subject: "Hello",
    Body: "Message body",
    Cc: null,
    InReplyTo: null
), ct);
```

### Move / Delete / Flag

These take a list of UIDs (as stored in the `messages` table):

```csharp
await backend.MoveAsync(accountId, uids, "INBOX", "Archive", ct);
await backend.DeleteAsync(accountId, uids, "INBOX", ct);
await backend.SetFlagsAsync(accountId, uids, "INBOX", MessageAction.MarkRead, ct);
```

## Adding OAuth for Your Provider

1. Register defaults in `OAuthProviderDefaults.cs` (use `CONFIGURE_ME` for client_id)
2. Users configure their credentials in `config.json` under `oauth_providers`
3. The dashboard's Add Account form automatically shows an OAuth button when a provider is configured
4. OAuth flow: popup -> consent -> callback -> token stored in DB -> `IOAuthAccessTokenProvider` refreshes automatically

## Testing Guidelines

1. **Unit test** your API client with mock HTTP responses
2. **Unit test** your sync backend with mock API client and real repositories (in-memory SQLite)
3. **Integration test** your backend against the real API (if possible) using a test account
4. Verify that synced data appears correctly in the MCP tools (list_folders, search_emails, get_message)
5. Test operation backends independently (send to a test mailbox, move between folders)

## Project Structure

```
src/AwesomeImapMcp.RestBackend/
├── CompositeBackendFactory.cs    # Routes accounts to backends
├── Imap/
│   ├── ImapSyncBackend.cs        # Wraps existing IMAP sync
│   └── ImapOperationBackend.cs   # Wraps existing IMAP/SMTP operations
└── Zoho/
    ├── ZohoApiClient.cs          # Low-level HTTP client
    ├── ZohoModels.cs             # JSON DTOs
    ├── ZohoSyncBackend.cs        # IEmailSyncBackend implementation
    ├── ZohoOperationBackend.cs   # IEmailOperationBackend implementation
    └── ZohoSyncService.cs        # BackgroundService for polling

src/AwesomeImapMcp.Core/Email/
├── IEmailSyncBackend.cs          # Sync interface
├── IEmailOperationBackend.cs     # Operations interface + EmailMessage + MessageAction
└── IEmailBackendFactory.cs       # Factory interface
```
