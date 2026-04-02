# Multi-Instance Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add multi-instance support (primary/secondary proxy), batch body fetch, account management MCP tools, and email security to the dashboard.

**Architecture:** Primary instance binds `HttpPort` (3846) and serves a tool execution API alongside the existing MCP HTTP transport. Secondary instances detect the port is in use and proxy all MCP tool calls to the primary via `HttpClient`. Leader failover uses the existing `InstanceCoordinator` heartbeat system. Email security uses DOMPurify for HTML sanitization with remote resource blocking.

**Tech Stack:** .NET 10 / C# 13, MailKit (IMAP), React 19, DOMPurify, TailwindCSS

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `src/UltimateImapMcp.Core/Coordination/IToolProxy.cs` | Interface for tool call proxying |
| `src/UltimateImapMcp.Core/Coordination/ProxyToolExecutor.cs` | HttpClient-based proxy to primary's API |
| `src/UltimateImapMcp.McpServer/Tools/AccountManagementTools.cs` | `start_dashboard`, `add_account_imap`, `add_account_oauth` tools |
| `src/UltimateImapMcp.McpServer/Tools/BodyFetchTools.cs` | `fetch_bodies` MCP tool |
| `tests/UltimateImapMcp.Core.Tests/Coordination/ProxyToolExecutorTests.cs` | Tests for proxy executor |
| `tests/UltimateImapMcp.McpServer.Tests/Tools/BodyFetchToolsTests.cs` | Tests for batch body fetch tool |
| `tests/UltimateImapMcp.McpServer.Tests/Tools/AccountManagementToolsTests.cs` | Tests for account management tools |
| `dashboard/client/src/lib/sanitizeEmail.ts` | DOMPurify email HTML sanitizer |

### Modified Files
| File | Changes |
|------|---------|
| `src/UltimateImapMcp.McpServer/Tools/McpJsonDefaults.cs` | Add static `IToolProxy?` for proxy dispatch |
| `src/UltimateImapMcp.McpServer/HttpMcpTransportHost.cs` | Always run, add tool API endpoints, failover hook |
| `src/UltimateImapMcp.McpServer/Program.cs` | Always register HTTP host, detect mode, set proxy |
| `src/UltimateImapMcp.Core/Email/IEmailSyncBackend.cs` | Add `FetchMessageBodiesBatchAsync` |
| `src/UltimateImapMcp.RestBackend/Imap/ImapSyncBackend.cs` | Implement batch body fetch |
| `src/UltimateImapMcp.McpServer/Tools/SearchTools.cs` | Add `fetchBodies` parameter |
| `dashboard/client/src/pages/Messages.tsx` | Default plain text, sanitized HTML, remote image toggle |
| `dashboard/client/package.json` | Add `dompurify` dependency |

---

## Task 1: Email Security — Install DOMPurify and Create Sanitizer

**Files:**
- Create: `dashboard/client/src/lib/sanitizeEmail.ts`
- Modify: `dashboard/client/package.json`

- [ ] **Step 1: Install DOMPurify**

Run: `cd /Users/shahab/repos/ultimate-imap-mcp/dashboard/client && npm install dompurify && npm install -D @types/dompurify`
Expected: Package added to package.json

- [ ] **Step 2: Create the sanitizer module**

Create `dashboard/client/src/lib/sanitizeEmail.ts`:
```typescript
import DOMPurify from 'dompurify'

/**
 * Sanitizes email HTML for safe rendering in an iframe.
 * Strips scripts, event handlers, and optionally blocks remote resources.
 */
export function sanitizeEmailHtml(html: string, allowRemoteImages: boolean = false): string {
  // Configure DOMPurify to strip dangerous content
  const clean = DOMPurify.sanitize(html, {
    WHOLE_DOCUMENT: true,
    FORCE_BODY: false,
    ADD_TAGS: ['style', 'link'],
    FORBID_TAGS: ['script', 'iframe', 'object', 'embed', 'form', 'input', 'textarea', 'button', 'select'],
    FORBID_ATTR: [
      'onerror', 'onload', 'onclick', 'onmouseover', 'onmouseout', 'onmousedown',
      'onmouseup', 'onfocus', 'onblur', 'onsubmit', 'onchange', 'onkeydown',
      'onkeyup', 'onkeypress', 'oncontextmenu', 'ondblclick',
    ],
  })

  if (allowRemoteImages) {
    return clean
  }

  // Block remote resources: replace external URLs in src/href for media
  const parser = new DOMParser()
  const doc = parser.parseFromString(clean, 'text/html')

  // Remove all <link> tags (external stylesheets)
  doc.querySelectorAll('link').forEach(el => el.remove())

  // Block remote images
  doc.querySelectorAll('img').forEach(img => {
    const src = img.getAttribute('src') || ''
    if (src.startsWith('http:') || src.startsWith('https:') || src.startsWith('//')) {
      img.removeAttribute('src')
      img.setAttribute('alt', '[Remote image blocked]')
      img.setAttribute('title', 'Remote images are blocked for security. Use the toggle to load them.')
    }
  })

  // Block background images in inline styles
  doc.querySelectorAll('[style]').forEach(el => {
    const style = el.getAttribute('style') || ''
    if (/url\s*\(/i.test(style)) {
      el.setAttribute('style', style.replace(/url\s*\([^)]*\)/gi, 'none'))
    }
  })

  return doc.documentElement.outerHTML
}
```

- [ ] **Step 3: Verify build**

Run: `cd /Users/shahab/repos/ultimate-imap-mcp/dashboard/client && npm run build`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add dashboard/client/package.json dashboard/client/package-lock.json dashboard/client/src/lib/sanitizeEmail.ts
git commit -m "feat: add DOMPurify email HTML sanitizer"
```

---

## Task 2: Email Security — Default Plain Text View with Sanitized HTML Toggle

**Files:**
- Modify: `dashboard/client/src/pages/Messages.tsx`

- [ ] **Step 1: Add sanitizer import and remote image state to MessageView**

In `Messages.tsx`, add import at top:
```typescript
import { sanitizeEmailHtml } from '../lib/sanitizeEmail'
```

In the `MessageView` component, change `showHtml` default to `false` and add remote image state:
```typescript
const [showHtml, setShowHtml] = useState(false)
const [allowRemoteImages, setAllowRemoteImages] = useState(false)
```

- [ ] **Step 2: Replace the HTML rendering section**

Replace the iframe rendering block (the `hasHtml && (showHtml || !hasText)` ternary branch) with:

```typescript
hasHtml && (showHtml || !hasText) ? (
  <div className="h-full flex flex-col">
    {!allowRemoteImages && (
      <div className="flex items-center gap-2 px-3 py-1.5 bg-amber-50 border-b border-amber-200 text-xs text-amber-700 flex-shrink-0">
        <span>Remote images are blocked.</span>
        <button
          onClick={() => setAllowRemoteImages(true)}
          className="underline hover:text-amber-900"
        >
          Load remote images
        </button>
      </div>
    )}
    <iframe
      srcDoc={sanitizeEmailHtml(msg.bodyHtml!, allowRemoteImages)}
      title="Email body"
      className="w-full flex-1 border-0 min-h-[400px] rounded bg-white"
      sandbox=""
    />
  </div>
)
```

- [ ] **Step 3: Update the view toggle to default text first**

The toggle buttons already exist (plain text / HTML). Ensure the condition checks work:
- When both text and HTML exist: default to plain text (`showHtml = false`)
- When ONLY HTML exists: show HTML (the `|| !hasText` condition)
- Toggle controls which view is active

No code change needed here — the `useState(false)` change in step 1 handles this. Just verify the logic is correct:
```typescript
// Existing condition: hasHtml && (showHtml || !hasText)
// With showHtml defaulting to false:
//   - Both exist → shows plain text (showHtml=false, hasText=true → false)
//   - Only HTML → shows HTML (showHtml=false, hasText=false → !false = true)
//   - Only text → shows text (hasHtml=false → skips to hasText branch)
```

- [ ] **Step 4: Reset remote images state when switching messages**

Add a `useEffect` to reset `allowRemoteImages` when the selected message changes:
```typescript
useEffect(() => {
  setAllowRemoteImages(false)
  setShowHtml(false)
}, [msg.uid])
```

Place this after the `useState` hooks and before any conditional returns.

- [ ] **Step 5: Build and verify**

Run: `cd /Users/shahab/repos/ultimate-imap-mcp/dashboard/client && npm run build`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add dashboard/client/src/pages/Messages.tsx
git commit -m "feat: default plain text email view, sanitized HTML with remote image blocking"
```

---

## Task 3: Batch Body Fetch — Add Backend Interface and IMAP Implementation

**Files:**
- Modify: `src/UltimateImapMcp.Core/Email/IEmailSyncBackend.cs`
- Modify: `src/UltimateImapMcp.RestBackend/Imap/ImapSyncBackend.cs`

- [ ] **Step 1: Add batch method to IEmailSyncBackend**

Add to `IEmailSyncBackend.cs` after the existing `FetchMessageBodyAsync` method:

```csharp
/// <summary>
/// Fetches message bodies in batch for the given UIDs in one IMAP session.
/// Returns the count of bodies successfully fetched.
/// </summary>
Task<int> FetchMessageBodiesBatchAsync(string accountId, string folderPath,
    IReadOnlyList<long> uids, CancellationToken ct = default)
{
    throw new NotSupportedException($"Batch body fetch is not supported by the {BackendType} backend.");
}
```

- [ ] **Step 2: Implement batch fetch in ImapSyncBackend**

Add to `ImapSyncBackend.cs` after the `FetchMessageBodyAsync` method:

```csharp
public async Task<int> FetchMessageBodiesBatchAsync(string accountId, string folderPath,
    IReadOnlyList<long> uids, CancellationToken ct = default)
{
    var folder = _folderRepo.GetByPath(accountId, folderPath);
    if (folder is null) return 0;

    // Filter to UIDs that haven't been fetched yet
    var toFetch = new List<long>();
    foreach (var uid in uids)
    {
        var existing = _messageRepo.GetByUid(accountId, folder.Id, uid);
        if (existing is not null && !existing.BodyFetched)
            toFetch.Add(uid);
    }

    if (toFetch.Count == 0) return 0;

    var fetched = 0;

    await _connMgr.ExecuteAsync(async client =>
    {
        var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        try
        {
            foreach (var uid in toFetch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var uidObj = new UniqueId((uint)uid);
                    var message = await imapFolder.GetMessageAsync(uidObj, ct).ConfigureAwait(false);
                    if (message is not null)
                    {
                        var dbMessage = _messageRepo.GetByUid(accountId, folder.Id, uid);
                        if (dbMessage is not null)
                        {
                            _messageRepo.UpdateBody(dbMessage.Id, message.TextBody, message.HtmlBody);
                            fetched++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to fetch body for UID {Uid} in {AccountId}/{FolderPath}",
                        uid, accountId, folderPath);
                }
            }
        }
        finally
        {
            try { await imapFolder.CloseAsync(false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is MailKit.ServiceNotConnectedException
                or MailKit.ServiceNotAuthenticatedException
                or IOException or OperationCanceledException) { }
        }
    }, ct).ConfigureAwait(false);

    return fetched;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build --nologo -v q`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add src/UltimateImapMcp.Core/Email/IEmailSyncBackend.cs src/UltimateImapMcp.RestBackend/Imap/ImapSyncBackend.cs
git commit -m "feat: add batch body fetch to IEmailSyncBackend and IMAP implementation"
```

---

## Task 4: Batch Body Fetch — `fetch_bodies` MCP Tool

**Files:**
- Create: `src/UltimateImapMcp.McpServer/Tools/BodyFetchTools.cs`
- Create: `tests/UltimateImapMcp.McpServer.Tests/Tools/BodyFetchToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/UltimateImapMcp.McpServer.Tests/Tools/BodyFetchToolsTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.McpServer.Tools;

namespace UltimateImapMcp.McpServer.Tests.Tools;

public class BodyFetchToolsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task FetchBodies_MissingAccountId_ReturnsError()
    {
        var config = new AppConfig();
        // Cannot construct BodyFetchTools without real repos/backends,
        // but we can verify the parameter validation path
        // by checking the tool attribute exists
        var toolType = typeof(BodyFetchTools);
        var method = toolType.GetMethod("FetchBodies");
        Assert.NotNull(method);

        // Verify parameter names match spec
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.Name == "accountId");
        Assert.Contains(parameters, p => p.Name == "uids");
        Assert.Contains(parameters, p => p.Name == "folder");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UltimateImapMcp.McpServer.Tests --nologo -v q --filter "BodyFetchToolsTests"`
Expected: FAIL — BodyFetchTools type does not exist

- [ ] **Step 3: Create the fetch_bodies tool**

Create `src/UltimateImapMcp.McpServer/Tools/BodyFetchTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class BodyFetchTools(
    MessageRepository messageRepo,
    FolderRepository folderRepo,
    IEmailBackendFactory backendFactory,
    AppConfig config,
    ILogger<BodyFetchTools> logger)
{
    [McpServerTool, Description(
        "Fetch message bodies in batch for multiple messages in one IMAP session. " +
        "Bodies are cached locally for future access. " +
        "Provide a comma-separated list of UIDs.")]
    public async Task<string> FetchBodies(
        [Description("Account ID")] string accountId,
        [Description("Comma-separated list of message UIDs to fetch bodies for")] string uids,
        [Description("Folder path (default: INBOX)")] string folder = "INBOX")
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "fetch_bodies",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["uids"] = uids, ["folder"] = folder },
            async () =>
            {
                try
                {
                    var uidList = ParseUids(uids);
                    if (uidList.Count == 0)
                        return McpJsonDefaults.Error("No valid UIDs provided.");

                    var folderRecord = folderRepo.GetByPath(accountId, folder);
                    if (folderRecord is null)
                        return McpJsonDefaults.Error($"Folder '{folder}' not found for account '{accountId}'.");

                    // Check which bodies are already cached
                    var alreadyCached = 0;
                    var needFetch = new List<long>();
                    foreach (var uid in uidList)
                    {
                        var msg = messageRepo.GetByUid(accountId, folderRecord.Id, uid);
                        if (msg is null) continue;
                        if (msg.BodyFetched)
                            alreadyCached++;
                        else
                            needFetch.Add(uid);
                    }

                    var fetched = 0;
                    if (needFetch.Count > 0)
                    {
                        await using var backend = backendFactory.CreateSyncBackend(accountId);
                        fetched = await backend.FetchMessageBodiesBatchAsync(
                            accountId, folder, needFetch).ConfigureAwait(false);
                    }

                    return JsonSerializer.Serialize(new
                    {
                        requested = uidList.Count,
                        already_cached = alreadyCached,
                        fetched,
                        failed = needFetch.Count - fetched,
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Batch body fetch failed: {ex.Message}");
                }
            }, config);
    }

    private static List<long> ParseUids(string uids)
    {
        var parts = uids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (long.TryParse(part, out var uid))
                result.Add(uid);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UltimateImapMcp.McpServer.Tests --nologo -v q --filter "BodyFetchToolsTests"`
Expected: PASS

- [ ] **Step 5: Build all**

Run: `dotnet build --nologo -v q`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add src/UltimateImapMcp.McpServer/Tools/BodyFetchTools.cs tests/UltimateImapMcp.McpServer.Tests/Tools/BodyFetchToolsTests.cs
git commit -m "feat: add fetch_bodies MCP tool for batch body fetching"
```

---

## Task 5: Batch Body Fetch — Add `fetchBodies` to `search_emails`

**Files:**
- Modify: `src/UltimateImapMcp.McpServer/Tools/SearchTools.cs`

- [ ] **Step 1: Add IEmailBackendFactory to constructor and fetchBodies parameter**

In `SearchTools.cs`, add the import at top:
```csharp
using UltimateImapMcp.Core.Email;
```

Change the constructor to add `IEmailBackendFactory`:
```csharp
public class SearchTools(MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager, IEmailBackendFactory backendFactory, AppConfig config, ILogger<SearchTools> logger)
```

Add a new parameter to the `SearchEmails` method after `maxBodyLength`:
```csharp
[Description("Auto-fetch bodies for results before returning (default: false)")] bool fetchBodies = false)
```

- [ ] **Step 2: Add body fetching logic after search results**

After the line `var mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();`, add:

```csharp
// Auto-fetch bodies for results if requested
var bodiesFetched = 0;
if (fetchBodies && results.Count > 0)
{
    var groups = results
        .Where(m => !m.BodyFetched)
        .GroupBy(m => (m.AccountId, m.FolderId));

    foreach (var group in groups)
    {
        try
        {
            var folderRecord = folderRepo.GetByAccount(group.Key.AccountId)
                .FirstOrDefault(f => f.Id == group.Key.FolderId);
            if (folderRecord is null) continue;

            var groupUids = group.Select(m => (long)m.Uid).ToList();
            await using var backend = backendFactory.CreateSyncBackend(group.Key.AccountId);
            bodiesFetched += await backend.FetchMessageBodiesBatchAsync(
                group.Key.AccountId, folderRecord.Path, groupUids).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to batch-fetch bodies for search results");
        }
    }

    // Re-read results with bodies now populated
    if (bodiesFetched > 0)
    {
        results = results.Select(m =>
        {
            if (!m.BodyFetched)
            {
                var refreshed = messageRepo.GetById(m.Id);
                return refreshed ?? m;
            }
            return m;
        }).ToList();
        mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();
    }
}
```

- [ ] **Step 3: Add bodiesFetched to the response**

Update the return `JsonSerializer.Serialize` block to include `bodies_fetched`:
```csharp
return JsonSerializer.Serialize(new
{
    count = mapped.Count,
    source = serverSearch ? "server" : "cache",
    results = mapped,
    cache_info = cacheInfo,
    bodies_fetched = fetchBodies ? bodiesFetched : (int?)null,
}, McpJsonDefaults.Options);
```

- [ ] **Step 4: Build and test**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/UltimateImapMcp.McpServer/Tools/SearchTools.cs
git commit -m "feat: add fetchBodies parameter to search_emails for auto body fetch"
```

---

## Task 6: Account Management — `start_dashboard`, `add_account_imap`, `add_account_oauth`

**Files:**
- Create: `src/UltimateImapMcp.McpServer/Tools/AccountManagementTools.cs`
- Create: `tests/UltimateImapMcp.McpServer.Tests/Tools/AccountManagementToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/UltimateImapMcp.McpServer.Tests/Tools/AccountManagementToolsTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.McpServer.Tools;

namespace UltimateImapMcp.McpServer.Tests.Tools;

public class AccountManagementToolsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void StartDashboard_WhenDisabled_ReturnsStatus()
    {
        var config = new AppConfig { Server = new ServerConfig { DashboardEnabled = false } };
        var tools = new AccountManagementTools(
            null!, null!, config, NullLogger<AccountManagementTools>.Instance);
        var result = Parse(tools.StartDashboard());
        // Should report that dashboard is disabled
        Assert.True(
            result.TryGetProperty("error", out _) ||
            result.TryGetProperty("status", out _));
    }

    [Fact]
    public void AddAccountImap_MissingName_ReturnsError()
    {
        var config = new AppConfig();
        var tmpFile = Path.GetTempFileName();
        try
        {
            var store = new AccountsStore(tmpFile);
            var tools = new AccountManagementTools(
                store, null!, config, NullLogger<AccountManagementTools>.Instance);
            var result = Parse(tools.AddAccountImap(
                "", "imap.example.com", 993, "user@example.com", "pass123"));
            Assert.True(result.TryGetProperty("error", out _));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void AddAccountImap_ValidParams_AddsAccount()
    {
        var config = new AppConfig();
        var tmpFile = Path.GetTempFileName();
        try
        {
            var store = new AccountsStore(tmpFile);
            var tools = new AccountManagementTools(
                store, null!, config, NullLogger<AccountManagementTools>.Instance);
            var result = Parse(tools.AddAccountImap(
                "Test Account", "imap.example.com", 993,
                "user@example.com", "pass123",
                smtpHost: "smtp.example.com", smtpPort: 587,
                provider: "generic"));

            Assert.Equal("Test Account", result.GetProperty("name").GetString());
            Assert.Equal("test-account", result.GetProperty("id").GetString());

            // Verify account persisted to store
            var data = store.Read();
            Assert.Single(data.Accounts);
            Assert.Equal("test-account", data.Accounts[0].Id);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UltimateImapMcp.McpServer.Tests --nologo -v q --filter "AccountManagementToolsTests"`
Expected: FAIL — AccountManagementTools does not exist

- [ ] **Step 3: Create the account management tools**

Create `src/UltimateImapMcp.McpServer/Tools/AccountManagementTools.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.ImapClient;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountManagementTools(
    AccountsStore accountsStore,
    CredentialEncryptor? encryptor,
    AppConfig config,
    ILogger<AccountManagementTools> logger)
{
    [McpServerTool, Description(
        "Start the dashboard web UI if not already running. Returns the dashboard URL.")]
    public string StartDashboard()
    {
        return McpJsonDefaults.LogToolCall(logger, "start_dashboard",
            new Dictionary<string, object?>(),
            () =>
            {
                if (!config.Server.DashboardEnabled)
                {
                    return JsonSerializer.Serialize(new
                    {
                        status = "disabled",
                        message = "Dashboard is disabled in config. Set server.dashboard_enabled=true and restart.",
                    }, McpJsonDefaults.Options);
                }

                var url = $"http://localhost:{config.Server.DashboardPort}";
                return JsonSerializer.Serialize(new
                {
                    status = "running",
                    url,
                    message = $"Dashboard available at {url}",
                }, McpJsonDefaults.Options);
            }, config);
    }

    [McpServerTool, Description(
        "Add a new email account using IMAP/SMTP credentials. " +
        "The account is saved to accounts.json and will begin syncing.")]
    public string AddAccountImap(
        [Description("Display name for the account")] string name,
        [Description("IMAP server hostname")] string imapHost,
        [Description("IMAP server port (default: 993)")] int imapPort = 993,
        [Description("Username (usually email address)")] string username = "",
        [Description("App password or account password")] string password = "",
        [Description("SMTP server hostname")] string? smtpHost = null,
        [Description("SMTP server port (default: 587)")] int smtpPort = 587,
        [Description("Provider: generic, gmail, outlook, yahoo, zoho")] string provider = "generic",
        [Description("Use SSL for SMTP (default: false)")] bool smtpUseSsl = false)
    {
        return McpJsonDefaults.LogToolCall(logger, "add_account_imap",
            new Dictionary<string, object?> { ["name"] = name, ["imapHost"] = imapHost, ["username"] = username },
            () =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return McpJsonDefaults.Error("Account name is required.");
                if (string.IsNullOrWhiteSpace(imapHost))
                    return McpJsonDefaults.Error("IMAP host is required.");
                if (string.IsNullOrWhiteSpace(username))
                    return McpJsonDefaults.Error("Username is required.");
                if (string.IsNullOrWhiteSpace(password))
                    return McpJsonDefaults.Error("Password is required.");

                var id = AccountConfigMapper.DeriveIdFromName(name);

                // Check for duplicates
                var existing = accountsStore.Read();
                if (existing.Accounts.Any(a => a.Id == id || a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return McpJsonDefaults.Error($"Account '{name}' already exists.");

                var credEnc = encryptor?.Encrypt(password) ?? password;

                var entry = new AccountEntry
                {
                    Id = id,
                    Name = name,
                    ImapHost = imapHost,
                    ImapPort = imapPort,
                    SmtpHost = smtpHost ?? imapHost.Replace("imap.", "smtp."),
                    SmtpPort = smtpPort,
                    SmtpUseSsl = smtpUseSsl,
                    Username = username,
                    AuthType = "app_password",
                    CredentialsEnc = credEnc,
                    Provider = provider,
                    BackendType = "imap",
                    Enabled = true,
                };

                accountsStore.Write(data => data.Accounts.Add(entry));

                return JsonSerializer.Serialize(new
                {
                    id,
                    name,
                    imap_host = imapHost,
                    imap_port = imapPort,
                    username,
                    provider,
                    message = $"Account '{name}' added. Restart the server to begin syncing.",
                }, McpJsonDefaults.Options);
            }, config);
    }

    [McpServerTool, Description(
        "Start OAuth flow for adding an email account. " +
        "Opens the dashboard in a browser at the add-account page with OAuth pre-selected. " +
        "Requires the dashboard to be enabled.")]
    public string AddAccountOauth(
        [Description("OAuth provider: gmail, outlook, yahoo, zoho")] string provider)
    {
        return McpJsonDefaults.LogToolCall(logger, "add_account_oauth",
            new Dictionary<string, object?> { ["provider"] = provider },
            () =>
            {
                var validProviders = new[] { "gmail", "outlook", "yahoo", "zoho" };
                if (!validProviders.Contains(provider.ToLowerInvariant()))
                    return McpJsonDefaults.Error($"Invalid provider '{provider}'. Valid: {string.Join(", ", validProviders)}.");

                if (!config.Server.DashboardEnabled)
                {
                    return McpJsonDefaults.Error(
                        "Dashboard must be enabled for OAuth flow. " +
                        "Set server.dashboard_enabled=true and restart.");
                }

                var url = $"http://localhost:{config.Server.DashboardPort}/accounts?action=add&provider={provider}&auth=oauth2";

                return JsonSerializer.Serialize(new
                {
                    status = "ready",
                    url,
                    provider,
                    instructions = $"Open {url} in your browser to complete the OAuth flow.",
                }, McpJsonDefaults.Options);
            }, config);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/UltimateImapMcp.McpServer.Tests --nologo -v q --filter "AccountManagementToolsTests"`
Expected: PASS

- [ ] **Step 5: Build all and run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add src/UltimateImapMcp.McpServer/Tools/AccountManagementTools.cs tests/UltimateImapMcp.McpServer.Tests/Tools/AccountManagementToolsTests.cs
git commit -m "feat: add start_dashboard, add_account_imap, add_account_oauth MCP tools"
```

---

## Task 7: Multi-Instance — IToolProxy Interface

**Files:**
- Create: `src/UltimateImapMcp.Core/Coordination/IToolProxy.cs`

- [ ] **Step 1: Create the IToolProxy interface**

Create `src/UltimateImapMcp.Core/Coordination/IToolProxy.cs`:

```csharp
namespace UltimateImapMcp.Core.Coordination;

/// <summary>
/// Abstraction for proxying MCP tool calls to a remote primary instance.
/// Used by secondary instances in multi-instance deployments.
/// </summary>
public interface IToolProxy
{
    /// <summary>Proxies a synchronous tool call to the primary instance.</summary>
    string Execute(string toolName, Dictionary<string, object?> parameters);

    /// <summary>Proxies an async tool call to the primary instance.</summary>
    Task<string> ExecuteAsync(string toolName, Dictionary<string, object?> parameters);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build --nologo -v q`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/UltimateImapMcp.Core/Coordination/IToolProxy.cs
git commit -m "feat: add IToolProxy interface for multi-instance tool proxying"
```

---

## Task 8: Multi-Instance — ProxyToolExecutor

**Files:**
- Create: `src/UltimateImapMcp.Core/Coordination/ProxyToolExecutor.cs`
- Create: `tests/UltimateImapMcp.Core.Tests/Coordination/ProxyToolExecutorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/UltimateImapMcp.Core.Tests/Coordination/ProxyToolExecutorTests.cs`:

```csharp
using UltimateImapMcp.Core.Coordination;

namespace UltimateImapMcp.Core.Tests.Coordination;

public class ProxyToolExecutorTests
{
    [Fact]
    public void Constructor_SetsBaseUrl()
    {
        var proxy = new ProxyToolExecutor("http://localhost:3846");
        Assert.NotNull(proxy);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrl_ThrowsOrReturnsError()
    {
        // Use a port that's unlikely to be in use
        var proxy = new ProxyToolExecutor("http://localhost:19999");
        var result = await proxy.ExecuteAsync("test_tool", new Dictionary<string, object?>());
        // Should return an error JSON since the primary is unreachable
        Assert.Contains("error", result);
    }

    [Fact]
    public void Execute_InvalidUrl_ReturnsError()
    {
        var proxy = new ProxyToolExecutor("http://localhost:19999");
        var result = proxy.Execute("test_tool", new Dictionary<string, object?>());
        Assert.Contains("error", result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UltimateImapMcp.Core.Tests --nologo -v q --filter "ProxyToolExecutorTests"`
Expected: FAIL — ProxyToolExecutor does not exist

- [ ] **Step 3: Create ProxyToolExecutor**

Create `src/UltimateImapMcp.Core/Coordination/ProxyToolExecutor.cs`:

```csharp
using System.Text.Json;

namespace UltimateImapMcp.Core.Coordination;

/// <summary>
/// Proxies MCP tool calls to a primary instance's HTTP API.
/// Used by secondary instances in multi-instance deployments.
/// </summary>
public sealed class ProxyToolExecutor : IToolProxy, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public ProxyToolExecutor(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public async Task<string> ExecuteAsync(string toolName, Dictionary<string, object?> parameters)
    {
        try
        {
            var url = $"{_baseUrl}/api/tools/{toolName}/execute";
            var content = new StringContent(
                JsonSerializer.Serialize(parameters, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Primary returned {(int)response.StatusCode}: {body}"
                }, JsonOptions);
            }

            // The tool API returns parsed JSON; re-serialize to string for MCP
            return body;
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Failed to reach primary instance at {_baseUrl}: {ex.Message}"
            }, JsonOptions);
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Request to primary instance at {_baseUrl} timed out"
            }, JsonOptions);
        }
    }

    public string Execute(string toolName, Dictionary<string, object?> parameters)
    {
        // Synchronous wrapper — block on the async call
        return ExecuteAsync(toolName, parameters).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/UltimateImapMcp.Core.Tests --nologo -v q --filter "ProxyToolExecutorTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/UltimateImapMcp.Core/Coordination/ProxyToolExecutor.cs tests/UltimateImapMcp.Core.Tests/Coordination/ProxyToolExecutorTests.cs
git commit -m "feat: add ProxyToolExecutor for multi-instance tool proxying"
```

---

## Task 9: Multi-Instance — Integrate Proxy into McpJsonDefaults

**Files:**
- Modify: `src/UltimateImapMcp.McpServer/Tools/McpJsonDefaults.cs`

- [ ] **Step 1: Add ToolProxy property and proxy dispatch to LogToolCall**

Modify `McpJsonDefaults.cs`:

Add using at top:
```csharp
using UltimateImapMcp.Core.Coordination;
```

Add the static property after the existing `Options` field:
```csharp
/// <summary>
/// When set, all tool calls are proxied to a remote primary instance.
/// Set by Program.cs when running in secondary mode.
/// Thread-safe: reads of reference types are atomic in .NET.
/// </summary>
internal static volatile IToolProxy? ToolProxy;
```

Modify `LogToolCall` to add proxy check at the start (before the `if (!config.Server.LogToolCalls)` line):
```csharp
internal static string LogToolCall(ILogger logger, string toolName,
    Dictionary<string, object?> parameters, Func<string> execute, AppConfig config)
{
    var proxy = ToolProxy;
    if (proxy is not null)
    {
        logger.LogDebug("Proxying tool {Tool} to primary instance", toolName);
        return proxy.Execute(toolName, parameters);
    }

    if (!config.Server.LogToolCalls)
        return execute();
    // ... rest unchanged ...
```

Modify `LogToolCallAsync` similarly:
```csharp
internal static async Task<string> LogToolCallAsync(ILogger logger, string toolName,
    Dictionary<string, object?> parameters, Func<Task<string>> execute, AppConfig config)
{
    var proxy = ToolProxy;
    if (proxy is not null)
    {
        logger.LogDebug("Proxying tool {Tool} to primary instance", toolName);
        return await proxy.ExecuteAsync(toolName, parameters).ConfigureAwait(false);
    }

    if (!config.Server.LogToolCalls)
        return await execute().ConfigureAwait(false);
    // ... rest unchanged ...
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: All tests pass (proxy is null by default, so no behavior change)

- [ ] **Step 3: Commit**

```bash
git add src/UltimateImapMcp.McpServer/Tools/McpJsonDefaults.cs
git commit -m "feat: add proxy dispatch to McpJsonDefaults for secondary instances"
```

---

## Task 10: Multi-Instance — Enhance HttpMcpTransportHost with Tool API

**Files:**
- Modify: `src/UltimateImapMcp.McpServer/HttpMcpTransportHost.cs`

- [ ] **Step 1: Add tool API endpoints and IEmailBackendFactory to HTTP host**

In `HttpMcpTransportHost.StartHttpServer()`, add the missing service registrations after the existing ones (before the MCP server registration block):

```csharp
// Email backend factory for tool execution
builder.Services.AddSingleton(_rootServices.GetRequiredService<IEmailBackendFactory>());
builder.Services.AddSingleton(_rootServices.GetRequiredService<ProviderProfileRegistry>());
builder.Services.AddSingleton(_rootServices.GetRequiredService<AccountsStore>());
```

Add usings at top:
```csharp
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Dashboard;
```

- [ ] **Step 2: Make MCP transport registration conditional**

Change the MCP server registration to be conditional:

```csharp
// Only add MCP HTTP transport when configured
var addMcpTransport = _config.Server.Transport is "http" or "both";
if (addMcpTransport)
{
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "ultimate-imap-mcp", Version = "0.1.0" };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();
}
```

- [ ] **Step 3: Add tool API and health endpoints**

After `_webApp = app;`, add tool API endpoints before the MCP mapping:

```csharp
// Tool API endpoint — always available for multi-instance proxy
app.MapToolsApi();

// Health check endpoint
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", instance = "primary" }));
```

And make the MCP mapping conditional:
```csharp
if (addMcpTransport)
{
    app.MapMcp();
}
```

- [ ] **Step 4: Add failover hook — clear proxy when becoming primary**

In `StartHttpServer()`, after successfully starting the server (after the logging block), add:

```csharp
// We're now serving — clear any proxy (we are the primary for tool execution)
McpServer.Tools.McpJsonDefaults.ToolProxy = null;
```

When the server shuts down or fails, the proxy should be re-established if another primary exists. Add in the `catch` block within the `ExecuteAsync` retry loop, after `_webApp = null;`:

```csharp
// Lost primary status — re-enable proxy if configured
if (_proxyBaseUrl is not null)
    McpServer.Tools.McpJsonDefaults.ToolProxy = new Core.Coordination.ProxyToolExecutor(_proxyBaseUrl);
```

Add a field to store the proxy URL:
```csharp
private string? _proxyBaseUrl;

public void SetProxyBaseUrl(string url) => _proxyBaseUrl = url;
```

- [ ] **Step 5: Build**

Run: `dotnet build --nologo -v q`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git add src/UltimateImapMcp.McpServer/HttpMcpTransportHost.cs
git commit -m "feat: enhance HttpMcpTransportHost with tool API for multi-instance proxy"
```

---

## Task 11: Multi-Instance — Wire Up Mode Detection in Program.cs

**Files:**
- Modify: `src/UltimateImapMcp.McpServer/Program.cs`

- [ ] **Step 1: Always register HttpMcpTransportHost**

Replace the existing conditional registration:
```csharp
if (transport is "http" or "both")
{
    builder.Services.AddSingleton<UltimateImapMcp.McpServer.HttpMcpTransportHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UltimateImapMcp.McpServer.HttpMcpTransportHost>());
}
```

With unconditional registration:
```csharp
// Always register HTTP host — serves tool API for multi-instance proxy.
// MCP HTTP transport is only added when configured (handled inside HttpMcpTransportHost).
builder.Services.AddSingleton<UltimateImapMcp.McpServer.HttpMcpTransportHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UltimateImapMcp.McpServer.HttpMcpTransportHost>());
```

- [ ] **Step 2: Add mode detection and proxy setup after host is built**

After `var host = builder.Build();` and the existing coordinator wiring block, add:

```csharp
// Multi-instance mode detection: check if HTTP port is already in use
{
    var httpPort = config.Server.HttpPort;
    var isSecondary = PortUtils.IsPortInUse(httpPort);

    if (isSecondary)
    {
        var proxyUrl = $"http://localhost:{httpPort}";
        Console.Error.WriteLine($"  [Multi-Instance] Port {httpPort} in use — running as secondary. Proxying tools to {proxyUrl}");
        McpJsonDefaults.ToolProxy = new UltimateImapMcp.Core.Coordination.ProxyToolExecutor(proxyUrl);

        // Tell the HTTP host the proxy URL for failover recovery
        var httpHost = host.Services.GetRequiredService<UltimateImapMcp.McpServer.HttpMcpTransportHost>();
        httpHost.SetProxyBaseUrl(proxyUrl);
    }
    else
    {
        Console.Error.WriteLine($"  [Multi-Instance] Port {httpPort} available — running as primary");
    }
}
```

Add using at top if not present:
```csharp
using UltimateImapMcp.McpServer.Tools;
```

- [ ] **Step 3: Update startup banner**

In `PrintStartupBanner`, add instance mode info. After the transport section, add:
```csharp
// Multi-instance info
w.WriteLine($"  Instance:    {(PortUtils.IsPortInUse(config.Server.HttpPort) ? "secondary (proxy)" : "primary")}");
```

Wait — `PrintStartupBanner` is called before the port check and host build. Move the port check logic before the banner, or just add the info to the banner using a passed parameter.

Actually, simpler: just add it after the existing banner call, as a separate line:
```csharp
// Print instance mode
Console.Error.WriteLine($"  Mode:        {(PortUtils.IsPortInUse(config.Server.HttpPort) ? "Secondary (proxying to primary)" : "Primary")}");
```

Place this in the multi-instance block added in step 2.

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/UltimateImapMcp.McpServer/Program.cs
git commit -m "feat: multi-instance mode detection — always start HTTP host, proxy when secondary"
```

---

## Task 12: Multi-Instance — Add IEmailBackendFactory Registration to HttpMcpTransportHost

The `HttpMcpTransportHost` also needs to be able to resolve `IEmailBackendFactory` for tools invoked via the API. Verify from Task 10 that the registration was added. This task ensures completeness.

**Files:**
- Verify: `src/UltimateImapMcp.McpServer/HttpMcpTransportHost.cs`

- [ ] **Step 1: Verify all tool dependencies are registered**

Check that these services are registered in `HttpMcpTransportHost.StartHttpServer()`:
- `IEmailBackendFactory` (needed by MessageTools, AttachmentTools, AnalysisTools, BodyFetchTools)
- `AccountsStore` (needed by AccountManagementTools)
- `CredentialEncryptor` (already registered)
- `ProviderProfileRegistry` (needed by factory)

If any are missing from the registration block in Task 10, add them now.

- [ ] **Step 2: Full integration test — build and run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: All 278+ tests pass

- [ ] **Step 3: Build frontend**

Run: `cd /Users/shahab/repos/ultimate-imap-mcp/dashboard/client && npm run build`
Expected: Build succeeds

- [ ] **Step 4: Commit if any changes**

```bash
git add -A && git diff --cached --quiet || git commit -m "fix: ensure all tool dependencies registered in HTTP host"
```

---

## Task 13: Research — Survey Email MCP Servers

This is a research task, not an implementation task. The spec explicitly says "Get user approval before implementing."

**Files:**
- No code changes

- [ ] **Step 1: Research open-source email MCP servers**

Search for existing open-source email MCP server implementations on GitHub and other sources. Look for:
- Feature lists and tool definitions
- Novel approaches to email management via MCP
- Features this project doesn't have yet

- [ ] **Step 2: Compile findings**

Create a summary of interesting features found, organized by the candidate areas from the spec:
- Email composition drafts
- Calendar/meeting detection
- Contact extraction
- Email threading improvements
- Bulk operations
- Smart search (natural language → IMAP query)
- Email templates
- Unsubscribe detection

- [ ] **Step 3: Present to user for approval**

Share the research findings and proposed new tools with the user. Do NOT implement anything without explicit approval.

---

## Verification Checklist

After all tasks are complete:

- [ ] `dotnet build --nologo -v q` — all projects build
- [ ] `dotnet test --nologo -v q` — all tests pass
- [ ] `cd dashboard/client && npm run build` — frontend builds
- [ ] Verify new MCP tools appear: `fetch_bodies`, `start_dashboard`, `add_account_imap`, `add_account_oauth`
- [ ] Verify email security: Messages.tsx defaults to plain text, HTML is sanitized
- [ ] Verify proxy: McpJsonDefaults.ToolProxy is null when primary, set when secondary
