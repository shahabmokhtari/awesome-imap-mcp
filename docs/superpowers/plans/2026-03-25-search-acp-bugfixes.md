# Enhanced Search, ACP Client Pool, and MCP Bug Fixes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add advanced search filters with server-side IMAP search, build a proper ACP client pool abstraction with Claude and Copilot implementations, and fix broken MCP tools.

**Architecture:** Three independent subsystems. (A) Enhanced search adds SQL-level filtering to `MessageRepository` and optional IMAP server search via `IEmailBackendFactory`. (B) ACP pool replaces the current single-client `AcpEmailAnalyzer` with a pooled, queue-based `IAcpClientPool` that serializes requests across N configurable client instances with per-provider implementations (Claude via `claude-agent-acp`, Copilot via `copilot --acp`). (C) Bug fixes patch `top_senders` and other broken tools.

**Tech Stack:** .NET 10, SQLite FTS5, MailKit IMAP SEARCH, System.Threading.Channels, System.Diagnostics.Metrics

---

## File Structure

### Subsystem A: Enhanced Search
| File | Responsibility |
|------|---------------|
| `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs` | Add `SearchAdvanced()` method with date/from/to/folder/subject/order filters |
| `src/AwesomeImapMcp.McpServer/Tools/SearchTools.cs` | Add new params to `SearchEmails`, add `serverSearch` flag |
| `src/AwesomeImapMcp.ImapClient/ImapSyncService.cs` | Add `ServerSearchAsync()` for live IMAP SEARCH |
| `src/AwesomeImapMcp.ImapClient/SyncManager.cs` | Expose `ServerSearchAsync()` through connection manager |
| `tests/AwesomeImapMcp.ImapClient.Tests/Repositories/MessageRepositoryTests.cs` | Tests for SearchAdvanced |

### Subsystem B: ACP Client Pool
| File | Responsibility |
|------|---------------|
| `src/AwesomeImapMcp.Llm/Acp/IAcpClientPool.cs` | Interface: `SendPromptAsync(prompt, model?, ct)` returns string |
| `src/AwesomeImapMcp.Llm/Acp/AcpClientPool.cs` | Pool implementation: N clients, Channel-based queue, serialized requests |
| `src/AwesomeImapMcp.Llm/Acp/AcpProviderFactory.cs` | Creates provider-specific AcpClient (claude vs copilot command/args) |
| `src/AwesomeImapMcp.Llm/Acp/AcpClient.cs` | Existing — minor changes (add metrics recording, verbose logging) |
| `src/AwesomeImapMcp.Llm/Acp/AcpEmailAnalyzer.cs` | Rewrite to use `IAcpClientPool` instead of managing its own client |
| `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs` | Expand `AcpConfig` with pool_size, provider-specific settings |
| `src/AwesomeImapMcp.Core/Telemetry.cs` | Add ACP histograms and counters |
| `src/AwesomeImapMcp.McpServer/Program.cs` | Register `IAcpClientPool` as singleton, wire DI |
| `src/AwesomeImapMcp.Dashboard/LlmApi.cs` | Use `IAcpClientPool` for test endpoint instead of one-off client |
| `tests/AwesomeImapMcp.Llm.Tests/Acp/AcpClientPoolTests.cs` | Pool behavior tests |

### Subsystem C: Bug Fixes
| File | Responsibility |
|------|---------------|
| `src/AwesomeImapMcp.McpServer/Tools/ReportTools.cs` | Fix `top_senders` — fall back to 0 days if no results with default window |
| `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs` | Fix `GetTopSenders` to handle edge cases |

---

## Task 1: Enhanced Search — `SearchAdvanced` in MessageRepository

**Files:**
- Modify: `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs`
- Test: `tests/AwesomeImapMcp.ImapClient.Tests/Repositories/MessageRepositoryTests.cs`

- [ ] **Step 1: Add `SearchAdvanced` method**

```csharp
// In MessageRepository.cs, after SearchFts method:

public record SearchFilter
{
    public string? Query { get; init; }
    public string? AccountId { get; init; }
    public int? FolderId { get; init; }
    public string? FromAddress { get; init; }
    public string? ToAddress { get; init; }
    public string? Subject { get; init; }
    public long? FromDateEpoch { get; init; }
    public long? ToDateEpoch { get; init; }
    public string OrderBy { get; init; } = "date_desc";
    public int MaxResults { get; init; } = 50;
}

public List<MessageRecord> SearchAdvanced(SearchFilter filter)
{
    using var conn = db.GetReadConnection();
    using var cmd = conn.CreateCommand();

    var conditions = new List<string>();
    var useFts = !string.IsNullOrEmpty(filter.Query);

    if (useFts) conditions.Add("messages_fts MATCH $query");
    if (filter.AccountId is not null) conditions.Add("m.account_id = $accountId");
    if (filter.FolderId is not null) conditions.Add("m.folder_id = $folderId");
    if (filter.FromAddress is not null) conditions.Add("m.from_email LIKE $from");
    if (filter.ToAddress is not null) conditions.Add("m.to_addresses LIKE $to");
    if (filter.Subject is not null) conditions.Add("m.subject LIKE $subject");
    if (filter.FromDateEpoch is not null) conditions.Add("m.date_epoch >= $fromDate");
    if (filter.ToDateEpoch is not null) conditions.Add("m.date_epoch <= $toDate");

    var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    var orderClause = filter.OrderBy switch
    {
        "date_asc" => "ORDER BY m.date_epoch ASC",
        "from" => "ORDER BY m.from_email ASC",
        "subject" => "ORDER BY m.subject ASC",
        "size_desc" => "ORDER BY m.size_bytes DESC",
        _ => "ORDER BY m.date_epoch DESC"
    };

    var join = useFts ? "JOIN messages_fts ON messages_fts.rowid = m.id" : "";

    cmd.CommandText = $"SELECT m.* FROM messages m {join} {where} {orderClause} LIMIT $limit;";

    if (useFts) cmd.Parameters.AddWithValue("$query", filter.Query);
    if (filter.AccountId is not null) cmd.Parameters.AddWithValue("$accountId", filter.AccountId);
    if (filter.FolderId is not null) cmd.Parameters.AddWithValue("$folderId", filter.FolderId);
    if (filter.FromAddress is not null) cmd.Parameters.AddWithValue("$from", $"%{filter.FromAddress}%");
    if (filter.ToAddress is not null) cmd.Parameters.AddWithValue("$to", $"%{filter.ToAddress}%");
    if (filter.Subject is not null) cmd.Parameters.AddWithValue("$subject", $"%{filter.Subject}%");
    if (filter.FromDateEpoch is not null) cmd.Parameters.AddWithValue("$fromDate", filter.FromDateEpoch);
    if (filter.ToDateEpoch is not null) cmd.Parameters.AddWithValue("$toDate", filter.ToDateEpoch);
    cmd.Parameters.AddWithValue("$limit", filter.MaxResults);

    using var reader = cmd.ExecuteReader();
    var list = new List<MessageRecord>();
    while (reader.Read())
        list.Add(ReadRecord(reader));
    return list;
}
```

- [ ] **Step 2: Write tests for SearchAdvanced**

```csharp
// In MessageRepositoryTests.cs, add these tests:

[Fact]
public void SearchAdvanced_ByFromAddress_FiltersCorrectly()
{
    var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
    InsertTestMessage("test", folderId, 801, "msg-801");
    // InsertTestMessage uses from_email = "test@test.com"

    var results = _messageRepo.SearchAdvanced(new SearchFilter
    {
        AccountId = "test",
        FromAddress = "test@test.com"
    });
    Assert.True(results.Count >= 1);
    Assert.All(results, r => Assert.Contains("test@test.com", r.FromEmail ?? ""));
}

[Fact]
public void SearchAdvanced_ByDateRange_FiltersCorrectly()
{
    var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
    InsertTestMessage("test", folderId, 802, "msg-802");
    // InsertTestMessage uses dateEpoch = 1774040400

    var results = _messageRepo.SearchAdvanced(new SearchFilter
    {
        AccountId = "test",
        FromDateEpoch = 1774040000,
        ToDateEpoch = 1774040500
    });
    Assert.True(results.Count >= 1);
}

[Fact]
public void SearchAdvanced_OrderByDateAsc_ReturnsAscending()
{
    var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
    // Insert messages — they'll have the same date, but test that the query runs
    InsertTestMessage("test", folderId, 803, "msg-803");

    var results = _messageRepo.SearchAdvanced(new SearchFilter
    {
        AccountId = "test",
        OrderBy = "date_asc"
    });
    Assert.NotEmpty(results);
}

[Fact]
public void SearchAdvanced_NoFilters_ReturnsAll()
{
    var results = _messageRepo.SearchAdvanced(new SearchFilter());
    Assert.NotEmpty(results);
}

[Fact]
public void SearchAdvanced_BySubject_FiltersCorrectly()
{
    var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
    InsertTestMessage("test", folderId, 804, "unique-subject-test");

    var results = _messageRepo.SearchAdvanced(new SearchFilter
    {
        Subject = "unique-subject-test"
    });
    Assert.True(results.Count >= 1);
}
```

- [ ] **Step 3: Run tests, verify pass**

Run: `dotnet test tests/AwesomeImapMcp.ImapClient.Tests`

- [ ] **Step 4: Commit**

```
feat: add SearchAdvanced with date/from/to/subject/folder/order filtering
```

---

## Task 2: Enhanced Search — Update MCP SearchEmails Tool

**Files:**
- Modify: `src/AwesomeImapMcp.McpServer/Tools/SearchTools.cs`

- [ ] **Step 1: Update SearchEmails with new parameters**

Replace the existing `SearchEmails` method with:

```csharp
[McpServerTool, Description(
    "Search emails with flexible filters. Searches local cache by default. " +
    "Use server_search=true to search directly on the IMAP server (slower but complete).")]
public async Task<string> SearchEmails(
    [Description("Search query text (FTS match for local, IMAP SEARCH for server)")] string? query = null,
    [Description("Account ID (required for server_search)")] string? accountId = null,
    [Description("Folder path to search in (e.g., INBOX)")] string? folder = null,
    [Description("Filter by sender email address")] string? from = null,
    [Description("Filter by recipient email address")] string? to = null,
    [Description("Filter by subject (substring match)")] string? subject = null,
    [Description("Start date (ISO 8601, e.g., 2026-01-01)")] string? fromDate = null,
    [Description("End date (ISO 8601, e.g., 2026-03-25)")] string? toDate = null,
    [Description("Sort order: date_desc (default), date_asc, from, subject, size_desc")] string order = "date_desc",
    [Description("Max results (default 20)")] int maxResults = 20,
    [Description("Summary only (default: true)")] bool summaryOnly = true,
    [Description("Search on IMAP server (default: false = local cache only)")] bool serverSearch = false,
    [Description("Max body length when summary_only=false (0=unlimited)")] int maxBodyLength = 0)
{
    try
    {
        // Parse dates to epoch
        long? fromEpoch = null, toEpoch = null;
        if (fromDate is not null && DateTimeOffset.TryParse(fromDate, out var fd))
            fromEpoch = fd.ToUnixTimeSeconds();
        if (toDate is not null && DateTimeOffset.TryParse(toDate, out var td))
            toEpoch = td.ToUnixTimeSeconds();

        // Resolve folder ID if folder path provided
        int? folderId = null;
        if (folder is not null && accountId is not null)
        {
            var folderRecord = folderRepo.GetByPath(accountId, folder);
            folderId = folderRecord?.Id;
        }

        List<MessageRecord> results;

        if (serverSearch)
        {
            if (string.IsNullOrEmpty(accountId))
                return Error("account_id is required for server_search.");

            results = await syncManager.ServerSearchAsync(
                accountId, folder ?? "INBOX", query, from, to, subject,
                fromEpoch, toEpoch, maxResults).ConfigureAwait(false);
        }
        else
        {
            results = messageRepo.SearchAdvanced(new SearchFilter
            {
                Query = query,
                AccountId = accountId,
                FolderId = folderId,
                FromAddress = from,
                ToAddress = to,
                Subject = subject,
                FromDateEpoch = fromEpoch,
                ToDateEpoch = toEpoch,
                OrderBy = order,
                MaxResults = maxResults,
            });
        }

        // Format results (same as existing)
        var mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();

        return JsonSerializer.Serialize(new
        {
            count = mapped.Count,
            source = serverSearch ? "server" : "cache",
            results = mapped,
        }, JsonOptions);
    }
    catch (Exception ex)
    {
        return Error($"Search failed: {ex.Message}");
    }
}
```

- [ ] **Step 2: Extract `FormatMessage` helper** (DRY with existing code)

```csharp
private static object FormatMessage(MessageRecord m, bool summaryOnly, int maxBodyLength)
{
    if (summaryOnly)
    {
        return new
        {
            id = m.Id, uid = m.Uid, folder_id = m.FolderId,
            subject = m.Subject, from = m.FromAddress,
            date = m.Date, snippet = m.Snippet,
            has_attachments = m.HasAttachments, thread_id = m.ThreadId
        };
    }

    var body = m.BodyText;
    if (maxBodyLength > 0 && body is not null && body.Length > maxBodyLength)
        body = body[..maxBodyLength] + "... [truncated]";

    return new
    {
        id = m.Id, uid = m.Uid, folder_id = m.FolderId,
        subject = m.Subject, from = m.FromAddress,
        to = m.ToAddresses, cc = m.CcAddresses,
        date = m.Date, body, body_fetched = m.BodyFetched,
        snippet = m.Snippet, has_attachments = m.HasAttachments,
        thread_id = m.ThreadId
    };
}
```

- [ ] **Step 3: Add DI for `FolderRepository` and `SyncManager`**

Update constructor: `public class SearchTools(MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager)`

- [ ] **Step 4: Commit**

```
feat: add advanced search filters and server_search to SearchEmails MCP tool
```

---

## Task 3: Enhanced Search — Server-Side IMAP Search

**Files:**
- Modify: `src/AwesomeImapMcp.ImapClient/ImapSyncService.cs`
- Modify: `src/AwesomeImapMcp.ImapClient/SyncManager.cs`

- [ ] **Step 1: Add `ServerSearchAsync` to ImapSyncService**

```csharp
// In ImapSyncService.cs, after SyncFolderMessagesAsync:

/// <summary>
/// Searches directly on the IMAP server and caches matching messages locally.
/// </summary>
public async Task<List<MessageRecord>> ServerSearchAsync(
    ImapClientLib client, string accountId, FolderRecord folder,
    string? query, string? from, string? to, string? subject,
    long? fromEpoch, long? toEpoch, int maxResults,
    CancellationToken ct = default)
{
    var imapFolder = await client.GetFolderAsync(folder.Path, ct).ConfigureAwait(false);
    if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
        return [];

    await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

    try
    {
        // Build IMAP search query
        SearchQuery searchQuery = SearchQuery.All;
        if (!string.IsNullOrEmpty(query))
            searchQuery = SearchQuery.And(searchQuery, SearchQuery.BodyContains(query));
        if (!string.IsNullOrEmpty(from))
            searchQuery = SearchQuery.And(searchQuery, SearchQuery.FromContains(from));
        if (!string.IsNullOrEmpty(to))
            searchQuery = SearchQuery.And(searchQuery, SearchQuery.ToContains(to));
        if (!string.IsNullOrEmpty(subject))
            searchQuery = SearchQuery.And(searchQuery, SearchQuery.SubjectContains(subject));
        if (fromEpoch is not null)
            searchQuery = SearchQuery.And(searchQuery,
                SearchQuery.SentAfter(DateTimeOffset.FromUnixTimeSeconds(fromEpoch.Value).DateTime));
        if (toEpoch is not null)
            searchQuery = SearchQuery.And(searchQuery,
                SearchQuery.SentBefore(DateTimeOffset.FromUnixTimeSeconds(toEpoch.Value).DateTime));

        var uids = await imapFolder.SearchAsync(searchQuery, ct).ConfigureAwait(false);

        // Limit results
        var limitedUids = uids.Take(maxResults).ToList();
        if (limitedUids.Count == 0) return [];

        // Check which are already cached
        var results = new List<MessageRecord>();
        var missingUids = new List<UniqueId>();

        foreach (var uid in limitedUids)
        {
            var cached = _messageRepo.GetByUid(accountId, folder.Id, uid.Id);
            if (cached is not null)
                results.Add(cached);
            else
                missingUids.Add(uid);
        }

        // Fetch and cache missing messages (reuse existing sync logic pattern)
        if (missingUids.Count > 0)
        {
            var fetchRequest = new FetchRequest(
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.Size |
                MessageSummaryItems.BodyStructure,
                new[] { "References" });

            var summaries = await imapFolder.FetchAsync(missingUids, fetchRequest, ct).ConfigureAwait(false);

            foreach (var summary in summaries)
            {
                ct.ThrowIfCancellationRequested();
                InsertMessageFromSummary(imapFolder, accountId, folder.Id, summary, ct);
                var inserted = _messageRepo.GetByUid(accountId, folder.Id, (long)summary.UniqueId.Id);
                if (inserted is not null) results.Add(inserted);
            }
        }

        return results;
    }
    finally
    {
        await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: Extract `InsertMessageFromSummary` helper from `SyncFolderMessagesAsync`**

Refactor the per-message insert logic (currently lines 148-254 of ImapSyncService.cs) into a reusable private method so both `SyncFolderMessagesAsync` and `ServerSearchAsync` share it.

- [ ] **Step 3: Add `ServerSearchAsync` to SyncManager**

```csharp
// In SyncManager.cs, public method for SearchTools to call:

public async Task<List<MessageRecord>> ServerSearchAsync(
    string accountId, string folderPath, string? query, string? from,
    string? to, string? subject, long? fromEpoch, long? toEpoch,
    int maxResults, CancellationToken ct = default)
{
    var account = appConfig.Accounts.FirstOrDefault(a =>
        accountRepo.GetById(accountId) is not null) is not null
        ? appConfig.Accounts.First()
        : throw new InvalidOperationException($"Account '{accountId}' not found.");

    var connMgr = GetOrCreateConnectionManager(accountId, account);
    var folder = folderRepo.GetByPath(accountId, folderPath)
        ?? throw new InvalidOperationException($"Folder '{folderPath}' not found.");

    return await connMgr.ExecuteAsync(async client =>
        await syncService.ServerSearchAsync(client, accountId, folder,
            query, from, to, subject, fromEpoch, toEpoch, maxResults, ct)
            .ConfigureAwait(false),
        ct).ConfigureAwait(false);
}
```

- [ ] **Step 4: Build, test, commit**

```
feat: add server-side IMAP search with automatic caching of results
```

---

## Task 4: ACP Client Pool — Config and Telemetry

**Files:**
- Modify: `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs`
- Modify: `src/AwesomeImapMcp.Core/Telemetry.cs`
- Modify: `config.example.json`

- [ ] **Step 1: Expand AcpConfig**

```csharp
/// <summary>Agent Client Protocol configuration for spawning LLM agents.</summary>
public class AcpConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "claude";

    [JsonPropertyName("pool_size")]
    public int PoolSize { get; set; } = 2;

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("claude")]
    public AcpProviderConfig Claude { get; set; } = new()
    {
        Command = "claude-agent-acp",
        Args = [],
    };

    [JsonPropertyName("copilot")]
    public AcpProviderConfig Copilot { get; set; } = new()
    {
        Command = "copilot",
        Args = ["--acp"],
    };

    // Legacy — keep for backward compat, maps to claude provider
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }
}

public class AcpProviderConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];
}
```

- [ ] **Step 2: Add ACP metrics to Telemetry.cs**

```csharp
// Add to Telemetry.cs:
public static readonly Histogram<double> AcpSessionLatency =
    Meter.CreateHistogram<double>("acp.session_ms", description: "ACP session creation latency");
public static readonly Histogram<double> AcpPromptLatency =
    Meter.CreateHistogram<double>("acp.prompt_ms", description: "ACP prompt processing latency");
public static readonly Counter<long> AcpRequests =
    Meter.CreateCounter<long>("acp.requests", description: "Total ACP requests");
public static readonly Counter<long> AcpErrors =
    Meter.CreateCounter<long>("acp.errors", description: "Total ACP errors");
```

- [ ] **Step 3: Update config.example.json**

Add `acp` section under `llm`:
```json
"acp": {
    "provider": "claude",
    "pool_size": 2,
    "request_timeout_seconds": 60,
    "claude": {
        "command": "claude-agent-acp",
        "args": []
    },
    "copilot": {
        "command": "copilot",
        "args": ["--acp"]
    }
}
```

- [ ] **Step 4: Commit**

```
feat: add ACP pool config (provider, pool_size, per-provider command) and telemetry
```

---

## Task 5: ACP Client Pool — Core Pool Implementation

**Files:**
- Create: `src/AwesomeImapMcp.Llm/Acp/IAcpClientPool.cs`
- Create: `src/AwesomeImapMcp.Llm/Acp/AcpClientPool.cs`
- Modify: `src/AwesomeImapMcp.Llm/Acp/AcpClient.cs`
- Test: `tests/AwesomeImapMcp.Llm.Tests/Acp/AcpClientPoolTests.cs`

- [ ] **Step 1: Create IAcpClientPool interface**

```csharp
namespace AwesomeImapMcp.Llm.Acp;

/// <summary>
/// Manages a pool of ACP agent processes. Requests are queued and dispatched
/// to the next available client. Each client processes one request at a time.
/// </summary>
public interface IAcpClientPool : IAsyncDisposable
{
    /// <summary>Send a prompt to the next available ACP agent.</summary>
    Task<AcpPromptResult> SendPromptAsync(string prompt, string? model = null,
        CancellationToken ct = default);

    /// <summary>Number of currently active (initialized) clients in the pool.</summary>
    int ActiveClients { get; }

    /// <summary>Number of requests waiting in the queue.</summary>
    int QueuedRequests { get; }
}

public record AcpPromptResult
{
    public required string Response { get; init; }
    public string? Model { get; init; }
    public long SessionLatencyMs { get; init; }
    public long PromptLatencyMs { get; init; }
    public string? Error { get; init; }
}
```

- [ ] **Step 2: Create AcpClientPool implementation**

```csharp
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core;
using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.Llm.Acp;

public sealed class AcpClientPool : IAcpClientPool
{
    private readonly AcpConfig _config;
    private readonly ILogger<AcpClientPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<AcpWorkItem> _queue;
    private readonly List<Task> _workerTasks = [];
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;

    public int ActiveClients { get; private set; }
    public int QueuedRequests => _queue.Reader.Count;

    public AcpClientPool(AcpConfig config, ILogger<AcpClientPool> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _queue = Channel.CreateUnbounded<AcpWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false, SingleWriter = false
        });

        // Start worker tasks (one per pool slot)
        var poolSize = Math.Max(1, config.PoolSize);
        for (var i = 0; i < poolSize; i++)
        {
            var workerId = i;
            _workerTasks.Add(Task.Run(() => WorkerLoopAsync(workerId, _shutdownCts.Token)));
        }

        _logger.LogInformation("ACP pool started with {PoolSize} workers, provider={Provider}",
            poolSize, config.Provider);
    }

    public async Task<AcpPromptResult> SendPromptAsync(string prompt, string? model = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workItem = new AcpWorkItem(prompt, model, ct);
        await _queue.Writer.WriteAsync(workItem, ct).ConfigureAwait(false);

        _logger.LogDebug("ACP request queued (queue depth: {Depth})", _queue.Reader.Count);
        Telemetry.AcpRequests.Add(1, new TagList { { "provider", _config.Provider } });

        return await workItem.Completion.Task.ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken shutdownToken)
    {
        var workerLogger = _loggerFactory.CreateLogger($"AcpWorker[{workerId}]");
        AcpClient? client = null;
        AcpSession? session = null;

        workerLogger.LogDebug("ACP worker {Id} started", workerId);

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(shutdownToken))
            {
                if (item.Ct.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.Ct);
                    continue;
                }

                try
                {
                    // Ensure client is alive
                    (client, session) = await EnsureClientAsync(
                        client, session, item.Model, workerId, workerLogger, item.Ct)
                        .ConfigureAwait(false);

                    // Send prompt
                    var promptSw = Stopwatch.StartNew();
                    workerLogger.LogDebug("ACP [{Id}] sending prompt ({Length} chars)",
                        workerId, item.Prompt.Length);
                    workerLogger.LogTrace("ACP [{Id}] prompt: {Prompt}", workerId, item.Prompt);

                    var responseText = new StringBuilder();
                    await foreach (var evt in client.SendPromptAsync(session, item.Prompt, item.Ct)
                        .ConfigureAwait(false))
                    {
                        if (evt.Type == AcpEventType.TextDelta && evt.Text is not null)
                            responseText.Append(evt.Text);
                        else if (evt.Type == AcpEventType.Complete && evt.Text is not null)
                            responseText.Append(evt.Text);
                        else if (evt.Type == AcpEventType.Error)
                        {
                            promptSw.Stop();
                            Telemetry.AcpErrors.Add(1, new TagList { { "provider", _config.Provider } });
                            item.Completion.TrySetResult(new AcpPromptResult
                            {
                                Response = "",
                                Error = evt.Error ?? "ACP agent returned an error",
                                PromptLatencyMs = promptSw.ElapsedMilliseconds,
                            });
                            continue;
                        }
                    }

                    promptSw.Stop();
                    var response = responseText.ToString();

                    Telemetry.AcpPromptLatency.Record(promptSw.ElapsedMilliseconds,
                        new TagList { { "provider", _config.Provider } });
                    workerLogger.LogDebug("ACP [{Id}] response ({Length} chars, {Ms}ms)",
                        workerId, response.Length, promptSw.ElapsedMilliseconds);
                    workerLogger.LogTrace("ACP [{Id}] response: {Response}", workerId, response);

                    item.Completion.TrySetResult(new AcpPromptResult
                    {
                        Response = response,
                        Model = item.Model,
                        PromptLatencyMs = promptSw.ElapsedMilliseconds,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    workerLogger.LogWarning(ex, "ACP [{Id}] request failed, resetting client", workerId);
                    Telemetry.AcpErrors.Add(1, new TagList { { "provider", _config.Provider } });

                    // Reset client for recovery
                    if (client is not null)
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        client = null;
                        session = null;
                        ActiveClients = Math.Max(0, ActiveClients - 1);
                    }

                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            workerLogger.LogDebug("ACP worker {Id} stopped", workerId);
        }
    }

    private async Task<(AcpClient Client, AcpSession Session)> EnsureClientAsync(
        AcpClient? client, AcpSession? session, string? model,
        int workerId, ILogger workerLogger, CancellationToken ct)
    {
        if (client is not null && session is not null)
            return (client, session);

        // Resolve command/args for the configured provider
        var (command, args) = ResolveProviderCommand(model);

        workerLogger.LogInformation("ACP [{Id}] spawning new agent: {Command} {Args}",
            workerId, command, string.Join(" ", args));

        var sessionSw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(_config.RequestTimeoutSeconds);
        client = new AcpClient(command, args, _loggerFactory.CreateLogger<AcpClient>(), timeout);

        await client.InitializeAsync(ct).ConfigureAwait(false);
        session = await client.CreateSessionAsync(Path.GetTempPath(), ct).ConfigureAwait(false);

        sessionSw.Stop();
        Telemetry.AcpSessionLatency.Record(sessionSw.ElapsedMilliseconds,
            new TagList { { "provider", _config.Provider } });
        workerLogger.LogInformation("ACP [{Id}] session created in {Ms}ms",
            workerId, sessionSw.ElapsedMilliseconds);

        ActiveClients++;
        return (client, session);
    }

    private (string Command, string[] Args) ResolveProviderCommand(string? model)
    {
        // Resolve from provider config or legacy fields
        var provider = _config.Provider.ToLowerInvariant();
        string command;
        var argsList = new List<string>();

        if (provider == "copilot")
        {
            command = _config.Copilot.Command;
            argsList.AddRange(_config.Copilot.Args);
        }
        else
        {
            // Claude or custom — check legacy config first, then new config
            if (!string.IsNullOrEmpty(_config.Command))
            {
                command = _config.Command;
                argsList.AddRange(_config.Args ?? []);
            }
            else
            {
                command = _config.Claude.Command;
                argsList.AddRange(_config.Claude.Args);
            }
        }

        if (!string.IsNullOrEmpty(model) && !argsList.Contains("--model"))
        {
            argsList.Add("--model");
            argsList.Add(model);
        }

        return (command, argsList.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.Writer.Complete();
        _shutdownCts.Cancel();

        try { await Task.WhenAll(_workerTasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _shutdownCts.Dispose();
        _logger.LogInformation("ACP pool shut down");
    }
}

file record AcpWorkItem(string Prompt, string? Model, CancellationToken Ct)
{
    public TaskCompletionSource<AcpPromptResult> Completion { get; } = new();
}
```

- [ ] **Step 3: Add verbose input/output logging to AcpClient**

In `AcpClient.cs`, change the `LogDebug` calls in `WriteRequestAsync` and `ReadEventsAsync` to include prompt content at Trace level (already done via the pool's trace logging).

- [ ] **Step 4: Commit**

```
feat: add ACP client pool with queue-based request serialization and metrics
```

---

## Task 6: ACP Client Pool — Wire into DI and Consumers

**Files:**
- Modify: `src/AwesomeImapMcp.Llm/Acp/AcpEmailAnalyzer.cs`
- Modify: `src/AwesomeImapMcp.McpServer/Program.cs`
- Modify: `src/AwesomeImapMcp.Dashboard/LlmApi.cs`
- Modify: `src/AwesomeImapMcp.Dashboard/DashboardHost.cs`

- [ ] **Step 1: Rewrite AcpEmailAnalyzer to use IAcpClientPool**

```csharp
namespace AwesomeImapMcp.Llm.Acp;

public class AcpEmailAnalyzer : IEmailAnalyzer
{
    private readonly IAcpClientPool _pool;
    private readonly ILogger<AcpEmailAnalyzer> _logger;

    public AcpEmailAnalyzer(IAcpClientPool pool, ILogger<AcpEmailAnalyzer> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public bool SupportsBackgroundAnalysis => true;

    public async Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(email, type);
        var result = await _pool.SendPromptAsync(prompt, ct: ct).ConfigureAwait(false);

        if (result.Error is not null)
        {
            _logger.LogError("ACP analysis error: {Error}", result.Error);
            return new AnalysisResult
            {
                Type = type,
                ResultJson = JsonSerializer.Serialize(new { error = result.Error }),
                ModelUsed = "acp"
            };
        }

        var json = ApiEmailAnalyzer.ExtractJson(result.Response);
        return new AnalysisResult
        {
            Type = type,
            ResultJson = json,
            ModelUsed = "acp"
        };
    }

    private static string BuildPrompt(EmailContent email, AnalysisType type)
    {
        var systemPrompt = ApiEmailAnalyzer.BuildSystemPrompt(type);
        var body = email.Body ?? email.Snippet ?? "(no body)";
        if (body.Length > 4000)
            body = body[..4000] + "... [truncated]";

        return $"""
            {systemPrompt}

            Email to analyze:
            Subject: {email.Subject}
            From: {email.From}
            Body:
            {body}

            Respond with ONLY the JSON object, no other text.
            """;
    }
}
```

- [ ] **Step 2: Register IAcpClientPool in Program.cs**

```csharp
// Replace the AcpEmailAnalyzer registration:

// Register ACP pool as singleton (shared across email analyzer and dashboard)
if (config.Llm.Provider.StartsWith("acp_", StringComparison.OrdinalIgnoreCase) && config.Llm.Enabled)
{
    builder.Services.AddSingleton<IAcpClientPool>(sp =>
        new AcpClientPool(
            config.Llm.Acp,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AcpClientPool>(),
            sp.GetRequiredService<ILoggerFactory>()));
}

// Email analyzer uses the pool
builder.Services.AddSingleton<IEmailAnalyzer>(sp =>
{
    var llmConfig = sp.GetRequiredService<LlmConfig>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    return llmConfig.Provider.ToLowerInvariant() switch
    {
        "anthropic" or "openai" when llmConfig.Enabled =>
            new ApiEmailAnalyzer(ChatClientFactory.Create(llmConfig), llmConfig.Model,
                loggerFactory.CreateLogger<ApiEmailAnalyzer>()),

        var p when (p == "acp_claude" or p == "acp_copilot") && llmConfig.Enabled =>
            new AcpEmailAnalyzer(
                sp.GetRequiredService<IAcpClientPool>(),
                loggerFactory.CreateLogger<AcpEmailAnalyzer>()),

        _ => new InContextAnalyzer()
    };
});
```

- [ ] **Step 3: Update LlmApi test endpoint to use pool**

Replace the `TestAcpProviderAsync` method in LlmApi.cs:

```csharp
private static async Task<IResult> TestAcpProviderAsync(
    string provider, string model, string prompt,
    LlmConfig llmConfig, IServiceProvider services)
{
    var pool = services.GetService<IAcpClientPool>();
    if (pool is null)
        return Results.Json(new { error = "ACP pool not initialized. Enable an ACP provider in settings." },
            statusCode: 500);

    var sw = Stopwatch.StartNew();
    var result = await pool.SendPromptAsync(prompt, model).ConfigureAwait(false);
    sw.Stop();

    if (result.Error is not null)
        return Results.Json(new { error = result.Error, model }, statusCode: 502);

    return Results.Ok(new
    {
        response = result.Response,
        model,
        duration_ms = sw.ElapsedMilliseconds,
    });
}
```

- [ ] **Step 4: Forward IAcpClientPool in DashboardHost.cs**

Add to the DI forwarding section:
```csharp
var acpPool = _rootServices.GetService<IAcpClientPool>();
if (acpPool is not null)
    builder.Services.AddSingleton(acpPool);
```

- [ ] **Step 5: Build, test, commit**

```
feat: wire ACP pool into DI, email analyzer, and dashboard test endpoint
```

---

## Task 7: Bug Fixes — top_senders and MCP Tools

**Files:**
- Modify: `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs`
- Modify: `src/AwesomeImapMcp.McpServer/Tools/ReportTools.cs`

- [ ] **Step 1: Fix GetTopSenders — try without date filter if no results**

The issue is likely that `date_epoch` is `NULL` for messages synced without date info, or the 30-day window is too restrictive. Fix by falling back:

```csharp
public List<TopSenderRecord> GetTopSenders(string accountId, int days = 30, int limit = 10)
{
    // Try with date filter first
    var results = GetTopSendersInternal(accountId, days, limit);
    if (results.Count > 0) return results;

    // Fall back to all-time if date-filtered returned nothing
    return GetTopSendersInternal(accountId, 0, limit);
}

private List<TopSenderRecord> GetTopSendersInternal(string accountId, int days, int limit)
{
    using var conn = db.GetReadConnection();
    using var cmd = conn.CreateCommand();

    var dateFilter = days > 0 ? "AND date_epoch >= $since" : "";
    cmd.CommandText = $"""
        SELECT from_email, COUNT(*) as msg_count,
               COALESCE(SUM(size_bytes), 0) as total_size
        FROM messages
        WHERE account_id = $accountId
          AND from_email IS NOT NULL AND from_email != ''
          {dateFilter}
        GROUP BY from_email
        ORDER BY msg_count DESC
        LIMIT $limit;
        """;
    cmd.Parameters.AddWithValue("$accountId", accountId);
    if (days > 0)
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("$limit", limit);

    using var reader = cmd.ExecuteReader();
    var list = new List<TopSenderRecord>();
    while (reader.Read())
    {
        list.Add(new TopSenderRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2)));
    }
    return list;
}
```

Key fix: added `AND from_email != ''` (empty strings pass `IS NOT NULL` check) and fall-back to all-time when date-filtered returns nothing.

- [ ] **Step 2: Update ReportTools.TopSenders to show time window used**

```csharp
// In the response, indicate if fallback was used:
return JsonSerializer.Serialize(new
{
    account_id = accountId,
    period_days = days,
    note = senders.Count > 0 && days > 0 ? null : "Showing all-time results (no data in specified period)",
    count = senders.Count,
    senders = senders.Select((s, i) => new { ... }).ToList()
}, JsonOptions);
```

- [ ] **Step 3: Build, test, commit**

```
fix: top_senders falls back to all-time when date window returns empty
```

---

## E2E Test Recipe

For each task:
1. `dotnet build` — 0 errors
2. `dotnet test` — all tests pass
3. For search: test via MCP tool call with various filter combinations
4. For ACP pool: test via dashboard LLM test endpoint with `acp_claude`/`acp_copilot`
5. For bug fixes: call `top_senders` via MCP and verify non-empty results
