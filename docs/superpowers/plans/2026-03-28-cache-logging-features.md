# Cache Stats, Body Caching, MCP Logging & Log Rotation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add cache statistics to the dashboard, ensure bodies are always cached when fetched, add configurable MCP tool-level + protocol-level logging, and implement log file rotation.

**Architecture:** Five independent features sharing config and UI surfaces. Cache stats adds a new API endpoint + dashboard card. Body caching ensures all code paths that fetch message bodies persist them. MCP logging adds tool-call interception and a stdio stream wrapper. Log rotation adds size-based pruning to the existing FileLoggerProvider.

**Tech Stack:** .NET 10 / C# 13, React/TypeScript, SQLite, MCP SDK (ModelContextProtocol)

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs` | Modify | Add `LogToolCalls`, `LogProtocol`, `LogDirMaxSizeMb` to ServerConfig |
| `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs` | Modify | Add `GetCacheStats()` and `GetCacheStatsByAccount()` methods |
| `src/AwesomeImapMcp.Dashboard/CacheApi.cs` | Modify | Add `GET /api/cache/stats` endpoint |
| `src/AwesomeImapMcp.Dashboard/SettingsApi.cs` | Modify | Expose new server config fields in GET/PUT, add `logToolCalls`, `logProtocol`, `logDirMaxSizeMb` |
| `dashboard/client/src/hooks/useApi.ts` | Modify | Add `useCacheStats()` hook |
| `dashboard/client/src/pages/Settings.tsx` | Modify | Add Cache Statistics card with per-account breakdown |
| `src/AwesomeImapMcp.Core/Logging/FileLoggerProvider.cs` | Modify | Add log directory size enforcement on flush |
| `src/AwesomeImapMcp.McpServer/Tools/McpJsonDefaults.cs` | Modify | Add `LogToolCall` helper method |
| `src/AwesomeImapMcp.McpServer/McpProtocolLogger.cs` | Create | Wrapping Stream for stdio protocol logging |
| `src/AwesomeImapMcp.McpServer/Program.cs` | Modify | Wire protocol logger stream, pass config to tool logging |

---

### Task 1: Add Config Properties for New Features

**Files:**
- Modify: `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs:44-78`
- Modify: `src/AwesomeImapMcp.Dashboard/SettingsApi.cs`

- [ ] **Step 1: Add new ServerConfig properties**

In `AppConfig.cs`, add to `ServerConfig` class after the existing `LogDir` property (around line 71):

```csharp
[JsonPropertyName("log_tool_calls")]
public bool LogToolCalls { get; set; } = true;

[JsonPropertyName("log_protocol")]
public bool LogProtocol { get; set; } = false;

[JsonPropertyName("log_dir_max_size_mb")]
public int LogDirMaxSizeMb { get; set; } = 100;
```

- [ ] **Step 2: Expose in SettingsApi GET response**

In `SettingsApi.cs`, in the `MapGet("/api/settings"` handler, add to the `server` anonymous object:

```csharp
config.Server.LogToolCalls,
config.Server.LogProtocol,
config.Server.LogDirMaxSizeMb,
```

- [ ] **Step 3: Add to ServerSettingsUpdate DTO**

In `SettingsApi.cs`, add to `ServerSettingsUpdate` record:

```csharp
public bool? LogToolCalls { get; init; }
public bool? LogProtocol { get; init; }
public int? LogDirMaxSizeMb { get; init; }
```

- [ ] **Step 4: Handle in PUT handler**

In `SettingsApi.cs`, in the `MapPut` handler, inside the `if (updates.Server is { } s)` block, add:

```csharp
if (s.LogToolCalls is { } ltc) { config.Server.LogToolCalls = ltc; changed.Add("log_tool_calls"); }
if (s.LogProtocol is { } lp) { config.Server.LogProtocol = lp; changed.Add("log_protocol"); }
if (s.LogDirMaxSizeMb is { } ldm)
{
    if (ldm <= 0) return Results.BadRequest(new { error = "log_dir_max_size_mb must be > 0" });
    config.Server.LogDirMaxSizeMb = ldm; changed.Add("log_dir_max_size_mb");
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: 0 errors, 0 warnings

- [ ] **Step 6: Commit**

```bash
git add src/AwesomeImapMcp.Core/Configuration/AppConfig.cs src/AwesomeImapMcp.Dashboard/SettingsApi.cs
git commit -m "feat: add config properties for tool logging, protocol logging, and log dir size limit"
```

---

### Task 2: Cache Statistics Backend

**Files:**
- Modify: `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs`
- Modify: `src/AwesomeImapMcp.Dashboard/CacheApi.cs`

- [ ] **Step 1: Add cache stats record types to MessageRepository.cs**

Add before the `ReadRecord` method at the bottom of the file:

```csharp
public record CacheStatsRecord(int TotalMessages, int BodiesFetched, long DbSizeBytes);

public record AccountCacheStatsRecord(
    string AccountId, string AccountName, int MessageCount, int BodiesFetched,
    string? OldestCachedAt, string? NewestCachedAt);
```

- [ ] **Step 2: Add GetCacheStats method**

```csharp
public CacheStatsRecord GetCacheStats()
{
    using var conn = db.GetReadConnection();

    using var countCmd = conn.CreateCommand();
    countCmd.CommandText = """
        SELECT COUNT(*), SUM(CASE WHEN body_fetched = 1 THEN 1 ELSE 0 END)
        FROM messages WHERE deleted_at IS NULL;
        """;
    using var reader = countCmd.ExecuteReader();
    reader.Read();
    var totalMessages = reader.GetInt32(0);
    var bodiesFetched = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);

    var dbSize = new FileInfo(db.DbPath).Length;

    return new CacheStatsRecord(totalMessages, bodiesFetched, dbSize);
}
```

Note: `db.DbPath` needs to be exposed. Add a public property `public string DbPath => _dbPath;` to the `AppDatabase` class if it doesn't already exist.

- [ ] **Step 3: Add GetCacheStatsByAccount method**

```csharp
public List<AccountCacheStatsRecord> GetCacheStatsByAccount(AccountRepository accountRepo)
{
    using var conn = db.GetReadConnection();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT m.account_id,
               COUNT(*) as msg_count,
               SUM(CASE WHEN body_fetched = 1 THEN 1 ELSE 0 END) as bodies,
               MIN(m.cached_at) as oldest,
               MAX(m.cached_at) as newest
        FROM messages m
        WHERE m.deleted_at IS NULL
        GROUP BY m.account_id;
        """;
    using var reader = cmd.ExecuteReader();
    var list = new List<AccountCacheStatsRecord>();
    var accounts = accountRepo.GetAll().ToDictionary(a => a.Id, a => a.Name);
    while (reader.Read())
    {
        var accountId = reader.GetString(0);
        list.Add(new AccountCacheStatsRecord(
            AccountId: accountId,
            AccountName: accounts.TryGetValue(accountId, out var name) ? name : accountId,
            MessageCount: reader.GetInt32(1),
            BodiesFetched: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            OldestCachedAt: reader.IsDBNull(3) ? null : reader.GetString(3),
            NewestCachedAt: reader.IsDBNull(4) ? null : reader.GetString(4)
        ));
    }
    return list;
}
```

- [ ] **Step 4: Expose DbPath on AppDatabase if needed**

Check `src/AwesomeImapMcp.Core/Database/Database.cs`. If `_dbPath` is private, add:

```csharp
public string DbPath => _dbPath;
```

- [ ] **Step 5: Add GET /api/cache/stats endpoint**

In `CacheApi.cs`, add before the `return app;`:

```csharp
app.MapGet("/api/cache/stats", (MessageRepository messageRepo, AccountRepository accountRepo,
    ILoggerFactory loggerFactory) =>
{
    try
    {
        var stats = messageRepo.GetCacheStats();
        var byAccount = messageRepo.GetCacheStatsByAccount(accountRepo);
        return Results.Ok(new
        {
            totalMessages = stats.TotalMessages,
            bodiesFetched = stats.BodiesFetched,
            dbSizeBytes = stats.DbSizeBytes,
            dbSizeMb = Math.Round(stats.DbSizeBytes / (1024.0 * 1024.0), 2),
            accounts = byAccount.Select(a => new
            {
                a.AccountId,
                a.AccountName,
                a.MessageCount,
                a.BodiesFetched,
                a.OldestCachedAt,
                a.NewestCachedAt
            }).ToList()
        });
    }
    catch (Exception ex)
    {
        var logger = loggerFactory.CreateLogger("CacheApi");
        logger.LogError(ex, "Failed to get cache stats");
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});
```

- [ ] **Step 6: Build and test**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs \
        src/AwesomeImapMcp.Dashboard/CacheApi.cs \
        src/AwesomeImapMcp.Core/Database/Database.cs
git commit -m "feat: add cache statistics API endpoint with per-account breakdown"
```

---

### Task 3: Cache Statistics Frontend

**Files:**
- Modify: `dashboard/client/src/hooks/useApi.ts`
- Modify: `dashboard/client/src/pages/Settings.tsx`

- [ ] **Step 1: Add useCacheStats hook**

In `useApi.ts`, add after the existing cache hooks:

```typescript
export interface CacheStats {
  totalMessages: number
  bodiesFetched: number
  dbSizeBytes: number
  dbSizeMb: number
  accounts: Array<{
    accountId: string
    accountName: string
    messageCount: number
    bodiesFetched: number
    oldestCachedAt: string | null
    newestCachedAt: string | null
  }>
}

export function useCacheStats() {
  return useQuery({
    queryKey: ['cache-stats'],
    queryFn: () => apiFetch<CacheStats>('/api/cache/stats'),
    refetchInterval: 30000,
  })
}
```

- [ ] **Step 2: Add CacheStatsCard component in Settings.tsx**

Import `useCacheStats` and `type CacheStats` at the top. Add a new component:

```tsx
function CacheStatsCard() {
  const { data: stats, isLoading, error } = useCacheStats()

  if (isLoading) return <div className="bg-white rounded-xl shadow p-6"><p className="text-gray-400">Loading cache stats...</p></div>
  if (error || !stats) return null

  const fmt = (n: number) => n.toLocaleString()
  const fmtDate = (d: string | null) => {
    if (!d) return '—'
    try { return new Date(d).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) }
    catch { return d }
  }

  return (
    <div className="bg-white rounded-xl shadow p-6">
      <h2 className="text-lg font-semibold mb-4">Cache Statistics</h2>
      <div className="grid grid-cols-3 gap-4 mb-4">
        <div className="text-center">
          <div className="text-2xl font-bold text-blue-600">{fmt(stats.totalMessages)}</div>
          <div className="text-xs text-gray-500">Total Messages</div>
        </div>
        <div className="text-center">
          <div className="text-2xl font-bold text-green-600">{fmt(stats.bodiesFetched)}</div>
          <div className="text-xs text-gray-500">Bodies Cached</div>
        </div>
        <div className="text-center">
          <div className="text-2xl font-bold text-purple-600">{stats.dbSizeMb} MB</div>
          <div className="text-xs text-gray-500">Database Size</div>
        </div>
      </div>
      {stats.accounts.length > 0 && (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-gray-500 border-b">
              <th className="py-1">Account</th>
              <th className="py-1 text-right">Messages</th>
              <th className="py-1 text-right">Bodies</th>
              <th className="py-1 text-right">Oldest</th>
              <th className="py-1 text-right">Newest</th>
            </tr>
          </thead>
          <tbody>
            {stats.accounts.map(a => (
              <tr key={a.accountId} className="border-b border-gray-100">
                <td className="py-1 truncate max-w-[160px]" title={a.accountId}>{a.accountName}</td>
                <td className="py-1 text-right">{fmt(a.messageCount)}</td>
                <td className="py-1 text-right">{fmt(a.bodiesFetched)}</td>
                <td className="py-1 text-right text-xs">{fmtDate(a.oldestCachedAt)}</td>
                <td className="py-1 text-right text-xs">{fmtDate(a.newestCachedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
```

- [ ] **Step 3: Render CacheStatsCard in the Settings page**

Find where the `cache` SectionCard is rendered and place `<CacheStatsCard />` just before it.

- [ ] **Step 4: Build frontend and verify**

Run: `cd dashboard/client && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 5: Commit**

```bash
git add dashboard/client/src/hooks/useApi.ts dashboard/client/src/pages/Settings.tsx
git commit -m "feat: add cache statistics card to dashboard settings page"
```

---

### Task 4: Ensure Bodies Are Always Cached When Fetched

**Files:**
- Modify: `src/AwesomeImapMcp.Dashboard/MessagesApi.cs` (verify fetch-body caches)
- Verify: `src/AwesomeImapMcp.RestBackend/Imap/ImapSyncBackend.cs` (already calls UpdateBody)
- Verify: `src/AwesomeImapMcp.McpServer/Tools/MessageTools.cs` (already re-reads from DB)

- [ ] **Step 1: Verify existing body caching paths**

The `ImapSyncBackend.FetchMessageBodyAsync` already calls `_messageRepo.UpdateBody(dbMessage.Id, bodyText, bodyHtml)` at line 108. The `MessageTools.GetMessage` calls `backend.FetchMessageBodyAsync()` which goes through this path. The dashboard's `POST /api/messages/{accountId}/{folderId}/{uid}/fetch-body` also calls `backend.FetchMessageBodyAsync()`. All three paths already cache bodies. **No change needed for existing paths.**

- [ ] **Step 2: Verify SearchTools doesn't bypass cache**

`SearchTools.SearchEmails` at line 70 returns cached data only — it never fetches from server. When `summaryOnly=false`, it returns `msg.BodyText` from cache (or null if not cached). The tool description already tells users to use `get_message` to fetch bodies. **No change needed.**

- [ ] **Step 3: Commit (verification only)**

No code changes needed — all paths already cache bodies through `ImapSyncBackend.FetchMessageBodyAsync` → `MessageRepository.UpdateBody`. Document this in a comment if desired.

---

### Task 5: MCP Tool-Level Logging

**Files:**
- Modify: `src/AwesomeImapMcp.McpServer/Tools/McpJsonDefaults.cs`
- Modify: All tool classes (pattern change)

- [ ] **Step 1: Add LogToolCall helper to McpJsonDefaults**

```csharp
using Microsoft.Extensions.Logging;
using System.Diagnostics;

// Add to McpJsonDefaults class:
private static readonly ILoggerFactory? _loggerFactory;
private static AppConfig? _config;

internal static void Configure(ILoggerFactory loggerFactory, AppConfig config)
{
    _loggerFactory = loggerFactory;
    _config = config;
}

internal static string LogToolCall(ILogger logger, string toolName,
    Dictionary<string, object?> parameters, Func<string> execute, AppConfig config)
{
    if (!config.Server.LogToolCalls)
        return execute();

    var sw = Stopwatch.StartNew();
    string? result = null;
    Exception? error = null;
    try
    {
        result = execute();
        return result;
    }
    catch (Exception ex)
    {
        error = ex;
        throw;
    }
    finally
    {
        sw.Stop();
        var paramJson = JsonSerializer.Serialize(parameters, Options);
        if (error is not null)
        {
            logger.LogWarning("MCP Tool {Tool} failed after {Duration}ms | Params: {Params} | Error: {Error}",
                toolName, sw.ElapsedMilliseconds, paramJson, error.Message);
        }
        else
        {
            logger.LogInformation("MCP Tool {Tool} completed in {Duration}ms | Params: {Params} | Response length: {Length}",
                toolName, sw.ElapsedMilliseconds, paramJson, result?.Length ?? 0);
            logger.LogDebug("MCP Tool {Tool} response: {Response}", toolName, result);
        }
    }
}

internal static async Task<string> LogToolCallAsync(ILogger logger, string toolName,
    Dictionary<string, object?> parameters, Func<Task<string>> execute, AppConfig config)
{
    if (!config.Server.LogToolCalls)
        return await execute().ConfigureAwait(false);

    var sw = Stopwatch.StartNew();
    string? result = null;
    Exception? error = null;
    try
    {
        result = await execute().ConfigureAwait(false);
        return result;
    }
    catch (Exception ex)
    {
        error = ex;
        throw;
    }
    finally
    {
        sw.Stop();
        var paramJson = JsonSerializer.Serialize(parameters, Options);
        if (error is not null)
        {
            logger.LogWarning("MCP Tool {Tool} failed after {Duration}ms | Params: {Params} | Error: {Error}",
                toolName, sw.ElapsedMilliseconds, paramJson, error.Message);
        }
        else
        {
            logger.LogInformation("MCP Tool {Tool} completed in {Duration}ms | Params: {Params} | Response length: {Length}",
                toolName, sw.ElapsedMilliseconds, paramJson, result?.Length ?? 0);
            logger.LogDebug("MCP Tool {Tool} response: {Response}", toolName, result);
        }
    }
}
```

- [ ] **Step 2: Inject AppConfig into tool classes that need logging**

Each tool class already has `ILogger<T>`. Add `AppConfig config` to the primary constructor of all tool classes. Then wrap the main logic of key tools (the ones most useful to log) with `McpJsonDefaults.LogToolCall/LogToolCallAsync`.

For example, in `SearchTools`:
```csharp
public class SearchTools(MessageRepository messageRepo, FolderRepository folderRepo,
    SyncManager syncManager, ILogger<SearchTools> logger, AppConfig config)
```

Then in `SearchEmails`:
```csharp
return McpJsonDefaults.LogToolCall(logger, "search_emails",
    new Dictionary<string, object?> { ["query"] = query, ["accountId"] = accountId },
    () => { /* existing implementation */ }, config);
```

Apply this pattern to all 13 tool classes. The wrapper is non-invasive — it just times and logs the call.

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add MCP tool-level call logging with configurable enable/disable"
```

---

### Task 6: MCP Protocol-Level Logging (Verbose)

**Files:**
- Create: `src/AwesomeImapMcp.McpServer/McpProtocolLogger.cs`
- Modify: `src/AwesomeImapMcp.McpServer/Program.cs`

- [ ] **Step 1: Create McpProtocolLogger stream wrapper**

```csharp
using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.McpServer;

/// <summary>
/// Wrapping stream that tees all read/write bytes to a logger.
/// Used to capture raw MCP JSON-RPC protocol traffic when verbose logging is enabled.
/// </summary>
public sealed class McpProtocolLogger(Stream inner, ILogger logger, string direction) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = inner.Read(buffer, offset, count);
        if (bytesRead > 0)
            LogBytes(buffer, offset, bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = await inner.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead > 0)
            LogBytes(buffer, offset, bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead > 0)
            LogBytes(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        LogBytes(buffer, offset, count);
        inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        LogBytes(buffer, offset, count);
        await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        LogBytes(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    private void LogBytes(byte[] buffer, int offset, int count)
    {
        var text = System.Text.Encoding.UTF8.GetString(buffer, offset, count).TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
            logger.LogDebug("[MCP.Protocol] {Direction}: {Data}", direction, text);
    }

    private void LogBytes(ReadOnlySpan<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data).TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
            logger.LogDebug("[MCP.Protocol] {Direction}: {Data}", direction, text);
    }
}
```

- [ ] **Step 2: Wire protocol logger in Program.cs**

In `Program.cs`, before `builder.Services.AddMcpServer(...)`, add:

```csharp
if (config.Server.LogProtocol)
{
    var protocolLoggerFactory = LoggerFactory.Create(b =>
        b.AddProvider(new AwesomeImapMcp.Core.Logging.SqliteLoggerProvider(appDb)));
    var protocolLogger = protocolLoggerFactory.CreateLogger("MCP.Protocol");

    var originalIn = Console.OpenStandardInput();
    var originalOut = Console.OpenStandardOutput();
    var loggingIn = new McpProtocolLogger(originalIn, protocolLogger, "IN");
    var loggingOut = new McpProtocolLogger(originalOut, protocolLogger, "OUT");
    Console.SetIn(new StreamReader(loggingIn));
    Console.SetOut(new StreamWriter(loggingOut) { AutoFlush = true });
}
```

Note: This must be done BEFORE `AddMcpServer` + `WithStdioServerTransport()` so the MCP SDK picks up the wrapped streams.

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/AwesomeImapMcp.McpServer/McpProtocolLogger.cs src/AwesomeImapMcp.McpServer/Program.cs
git commit -m "feat: add verbose MCP protocol-level logging via stdio stream wrapper"
```

---

### Task 7: Log File Rotation (Size Limit)

**Files:**
- Modify: `src/AwesomeImapMcp.Core/Logging/FileLoggerProvider.cs`

- [ ] **Step 1: Add size enforcement to FileLoggerProvider**

Add a new field and constructor parameter:

```csharp
private readonly long _maxDirSizeBytes;

public FileLoggerProvider(string logDir, string instanceId, int maxDirSizeMb = 100)
{
    _logDir = logDir;
    _instanceId = instanceId;
    _maxDirSizeBytes = maxDirSizeMb * 1024L * 1024L;
    _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
}
```

- [ ] **Step 2: Add EnforceDirectorySize method**

```csharp
private void EnforceDirectorySize()
{
    try
    {
        var dir = new DirectoryInfo(_logDir);
        if (!dir.Exists) return;

        var files = dir.GetFiles("*.log", SearchOption.AllDirectories)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        var totalSize = files.Sum(f => f.Length);
        var index = 0;

        while (totalSize > _maxDirSizeBytes && index < files.Count)
        {
            var file = files[index];
            // Don't delete the current instance's active log files
            if (file.Name.StartsWith(_instanceId))
            {
                index++;
                continue;
            }
            totalSize -= file.Length;
            try { file.Delete(); }
            catch { /* best effort */ }
            index++;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FileLoggerProvider] Directory size enforcement failed: {ex.Message}");
    }
}
```

- [ ] **Step 3: Call EnforceDirectorySize after Flush**

In the `Flush()` method, add at the end (after writing all entries):

```csharp
// Periodically enforce log directory size limit
if (_maxDirSizeBytes > 0)
    EnforceDirectorySize();
```

Actually, calling this on every flush (every 2s) is too frequent. Use a counter:

Add a field: `private int _flushCount;`

Then in `Flush()`, after writing:
```csharp
// Check directory size every 30 flushes (~60 seconds)
if (_maxDirSizeBytes > 0 && ++_flushCount % 30 == 0)
    EnforceDirectorySize();
```

- [ ] **Step 4: Update Program.cs to pass maxDirSizeMb**

Find where `FileLoggerProvider` is constructed in Program.cs and pass `config.Server.LogDirMaxSizeMb`:

```csharp
new FileLoggerProvider(logDir, instanceInfo.Id, config.Server.LogDirMaxSizeMb)
```

- [ ] **Step 5: Build and test**

Run: `dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/AwesomeImapMcp.Core/Logging/FileLoggerProvider.cs src/AwesomeImapMcp.McpServer/Program.cs
git commit -m "feat: add log directory size enforcement with configurable limit (default 100MB)"
```

---

### Task 8: Frontend — Expose New Logging Settings

**Files:**
- Modify: `dashboard/client/src/pages/Settings.tsx`

- [ ] **Step 1: Add display labels for new fields**

The `SectionCard` component auto-renders fields. The new `logToolCalls`, `logProtocol`, `logDirMaxSizeMb` fields will appear automatically in the Server section since they're returned by `GET /api/settings`.

However, we should ensure booleans render as toggles and the labels are clear. The `fieldLabel` function already converts camelCase to spaced words, so `logToolCalls` → "Log Tool Calls", `logProtocol` → "Log Protocol", `logDirMaxSizeMb` → "Log Dir Max Size Mb". These are readable enough.

No code change needed — the SectionCard auto-renders new fields from the settings response.

- [ ] **Step 2: Verify frontend builds**

Run: `cd dashboard/client && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 3: Commit (if any changes needed)**

Only commit if manual adjustments were needed.

---

### Task 9: Final Integration Test

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: 0 errors

- [ ] **Step 2: All tests**

Run: `dotnet test`
Expected: All 318+ tests pass

- [ ] **Step 3: Frontend build**

Run: `cd dashboard/client && npm run build`
Expected: Build succeeds

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat: cache stats, MCP tool/protocol logging, log rotation — integration complete"
```
