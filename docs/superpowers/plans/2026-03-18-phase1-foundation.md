# Phase 1: Foundation Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a working MCP server that connects to any IMAP provider, caches messages in SQLite with FTS5, and exposes read-only tools (list_accounts, list_folders, search_emails, get_message, get_thread, get_folder_stats).

**Architecture:** Single .NET host process with MailKit for IMAP, Microsoft.Data.Sqlite for cache with FTS5 full-text search, and the official MCP C# SDK for tool registration over stdio transport. Three class library projects (Core, ImapClient, McpServer) with xUnit tests.

**Tech Stack:** .NET 10, C#, MailKit/MimeKit, Microsoft.Data.Sqlite, ModelContextProtocol (official C# SDK v1.0), xUnit, System.Text.Json

**Spec reference:** `docs/superpowers/specs/2026-03-18-awesome-imap-mcp-design.md`
**Schema reference:** `docs/DATA_MODEL.md`

---

## File Structure

### Source Projects

```
src/
  AwesomeImapMcp.Core/
    AwesomeImapMcp.Core.csproj
    Configuration/
      AppConfig.cs                  # Top-level config model (ServerConfig, AccountConfig, CacheConfig, etc.)
      ConfigLoader.cs               # JSON loading, env var substitution, validation, defaults
    Database/
      Database.cs                   # SQLite connection management (WAL, read pool, single writer)
      MigrationRunner.cs            # Applies numbered .sql migrations, tracks schema_version
      Migrations/
        001_initial.sql             # accounts, folders, messages, messages_fts, attachments tables
    Encryption/
      CredentialEncryptor.cs        # AES-256-GCM encrypt/decrypt with PBKDF2 key derivation
      MachineId.cs                  # Cross-platform machine ID (Windows: MachineGuid, Mac: IOPlatformUUID, Linux: /etc/machine-id)
    Types/
      Enums.cs                     # FolderRole, AuthMethod, ProviderType enums
    Providers/
      ProviderProfile.cs            # ProviderProfile record type + SearchCapabilities flags
      ProviderProfileRegistry.cs    # Registry with auto-detection from hostname

  AwesomeImapMcp.ImapClient/
    AwesomeImapMcp.ImapClient.csproj
    ImapConnectionManager.cs        # MailKit ImapClient wrapper, connect/auth (pooling + backoff deferred to Phase 3)
    FolderMapper.cs                 # Maps FolderRole to provider-specific IMAP paths
    MessageParser.cs                # Converts MailKit IMessageSummary → domain CachedMessage
    ThreadBuilder.cs                # Computes thread_id from References/In-Reply-To chain
    ImapSyncService.cs              # Initial sync: connect, list folders, fetch headers, populate cache
    Repositories/
      AccountRepository.cs          # CRUD for accounts table
      FolderRepository.cs           # CRUD for folders table
      MessageRepository.cs          # CRUD + FTS queries for messages table
      AttachmentRepository.cs       # CRUD for attachments table

  AwesomeImapMcp.McpServer/
    AwesomeImapMcp.McpServer.csproj
    Program.cs                      # Entry point: Host builder, DI, MCP server with stdio
    Tools/
      AccountTools.cs               # list_accounts, get_account_status
      FolderTools.cs                # list_folders, get_folder_stats
      SearchTools.cs                # search_emails (cache-first + IMAP fallback)
      MessageTools.cs               # get_message (lazy body), get_thread
```

### Test Projects

```
tests/
  AwesomeImapMcp.Core.Tests/
    AwesomeImapMcp.Core.Tests.csproj
    Configuration/
      ConfigLoaderTests.cs
    Database/
      DatabaseTests.cs
      MigrationRunnerTests.cs
    Encryption/
      CredentialEncryptorTests.cs
    Providers/
      ProviderProfileRegistryTests.cs

  AwesomeImapMcp.ImapClient.Tests/
    AwesomeImapMcp.ImapClient.Tests.csproj
    FolderMapperTests.cs
    MessageParserTests.cs
    ThreadBuilderTests.cs
    Repositories/
      AccountRepositoryTests.cs
      FolderRepositoryTests.cs
      MessageRepositoryTests.cs
```

---

## Chunk 1: Solution Scaffold + Core Types + Configuration

### Task 1: Solution Scaffold

Create the .NET solution with all projects, references, and shared build configuration.

**Files:**
- Create: `Directory.Build.props`
- Create: `AwesomeImapMcp.sln`
- Create: `src/AwesomeImapMcp.Core/AwesomeImapMcp.Core.csproj`
- Create: `src/AwesomeImapMcp.ImapClient/AwesomeImapMcp.ImapClient.csproj`
- Create: `src/AwesomeImapMcp.McpServer/AwesomeImapMcp.McpServer.csproj`
- Create: `tests/AwesomeImapMcp.Core.Tests/AwesomeImapMcp.Core.Tests.csproj`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/AwesomeImapMcp.ImapClient.Tests.csproj`

- [ ] **Step 1: Create Directory.Build.props**

```xml
<!-- Directory.Build.props (repo root) -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create the solution and source projects**

```bash
dotnet new sln -n AwesomeImapMcp

# Source projects
dotnet new classlib -n AwesomeImapMcp.Core -o src/AwesomeImapMcp.Core
dotnet new classlib -n AwesomeImapMcp.ImapClient -o src/AwesomeImapMcp.ImapClient
dotnet new console -n AwesomeImapMcp.McpServer -o src/AwesomeImapMcp.McpServer

# Test projects
dotnet new xunit -n AwesomeImapMcp.Core.Tests -o tests/AwesomeImapMcp.Core.Tests
dotnet new xunit -n AwesomeImapMcp.ImapClient.Tests -o tests/AwesomeImapMcp.ImapClient.Tests

# Add all to solution
dotnet sln add src/AwesomeImapMcp.Core
dotnet sln add src/AwesomeImapMcp.ImapClient
dotnet sln add src/AwesomeImapMcp.McpServer
dotnet sln add tests/AwesomeImapMcp.Core.Tests
dotnet sln add tests/AwesomeImapMcp.ImapClient.Tests
```

- [ ] **Step 3: Add project references**

```bash
# ImapClient depends on Core
dotnet add src/AwesomeImapMcp.ImapClient reference src/AwesomeImapMcp.Core

# McpServer depends on Core + ImapClient
dotnet add src/AwesomeImapMcp.McpServer reference src/AwesomeImapMcp.Core
dotnet add src/AwesomeImapMcp.McpServer reference src/AwesomeImapMcp.ImapClient

# Test projects reference their targets
dotnet add tests/AwesomeImapMcp.Core.Tests reference src/AwesomeImapMcp.Core
dotnet add tests/AwesomeImapMcp.ImapClient.Tests reference src/AwesomeImapMcp.Core
dotnet add tests/AwesomeImapMcp.ImapClient.Tests reference src/AwesomeImapMcp.ImapClient
```

- [ ] **Step 4: Add NuGet packages**

```bash
# Core
dotnet add src/AwesomeImapMcp.Core package Microsoft.Data.Sqlite
dotnet add src/AwesomeImapMcp.Core package System.Text.Json

# ImapClient
dotnet add src/AwesomeImapMcp.ImapClient package MailKit

# McpServer
dotnet add src/AwesomeImapMcp.McpServer package ModelContextProtocol
dotnet add src/AwesomeImapMcp.McpServer package Microsoft.Extensions.Hosting
```

- [ ] **Step 5: Delete auto-generated Class1.cs files, verify build**

```bash
rm src/AwesomeImapMcp.Core/Class1.cs
rm src/AwesomeImapMcp.ImapClient/Class1.cs
dotnet build
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 6: Verify tests run**

```bash
dotnet test
```

Expected: All default template tests pass (or 0 tests if xUnit template has none).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: scaffold .NET solution with Core, ImapClient, McpServer projects"
```

---

### Task 2: Core Types and Enums

**Files:**
- Create: `src/AwesomeImapMcp.Core/Types/Enums.cs`
- Create: `src/AwesomeImapMcp.Core/Providers/ProviderProfile.cs`

- [ ] **Step 1: Create enums**

```csharp
// src/AwesomeImapMcp.Core/Types/Enums.cs
namespace AwesomeImapMcp.Core.Types;

public enum FolderRole
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Spam,
    Archive
}

public enum AuthMethod
{
    Password,
    AppPassword,
    OAuth2
}

public enum ProviderType
{
    Gmail,
    Outlook,
    Fastmail,
    ProtonMail,
    Yahoo,
    Generic
}

[Flags]
public enum SearchCapabilities
{
    None = 0,
    BasicSearch = 1,
    BodySearch = 2,
    FuzzySearch = 4,
    SortExtension = 8,
    ThreadExtension = 16
}
```

- [ ] **Step 2: Create ProviderProfile record**

```csharp
// src/AwesomeImapMcp.Core/Providers/ProviderProfile.cs
namespace AwesomeImapMcp.Core.Providers;

using AwesomeImapMcp.Core.Types;

public record ProviderProfile
{
    public required ProviderType Type { get; init; }
    public required string Name { get; init; }
    public required Dictionary<FolderRole, string> FolderMap { get; init; }
    public required AuthMethod[] SupportedAuth { get; init; }
    public required SearchCapabilities Search { get; init; }
    public int MaxConnections { get; init; } = 3;
    public bool RequiresTlsTrust { get; init; } = false;
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(core): add type enums and ProviderProfile record"
```

---

### Task 3: Configuration

**Files:**
- Create: `src/AwesomeImapMcp.Core/Configuration/AppConfig.cs`
- Create: `src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs`
- Create: `tests/AwesomeImapMcp.Core.Tests/Configuration/ConfigLoaderTests.cs`

- [ ] **Step 1: Write failing tests for ConfigLoader**

```csharp
// tests/AwesomeImapMcp.Core.Tests/Configuration/ConfigLoaderTests.cs
using AwesomeImapMcp.Core.Configuration;

namespace AwesomeImapMcp.Core.Tests.Configuration;

public class ConfigLoaderTests
{
    [Fact]
    public void Load_ValidJson_ReturnsConfig()
    {
        var json = """
        {
          "server": {
            "transport": "stdio",
            "dashboard_port": 3847,
            "dashboard_enabled": false
          },
          "accounts": [
            {
              "name": "personal",
              "imap_host": "imap.gmail.com",
              "imap_port": 993,
              "username": "test@gmail.com",
              "auth_type": "app_password",
              "provider": "gmail"
            }
          ],
          "cache": {
            "db_path": "/tmp/test.db",
            "max_size_mb": 500
          }
        }
        """;

        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);

        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("stdio", config.Server.Transport);
            Assert.Equal(3847, config.Server.DashboardPort);
            Assert.False(config.Server.DashboardEnabled);
            Assert.Single(config.Accounts);
            Assert.Equal("personal", config.Accounts[0].Name);
            Assert.Equal("imap.gmail.com", config.Accounts[0].ImapHost);
            Assert.Equal(500, config.Cache.MaxSizeMb);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(
            () => ConfigLoader.LoadFromFile("/nonexistent/config.json"));
    }

    [Fact]
    public void Load_EmptyAccounts_DefaultsApplied()
    {
        var json = """
        {
          "accounts": []
        }
        """;

        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);

        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("stdio", config.Server.Transport);
            Assert.Equal(3847, config.Server.DashboardPort);
            Assert.True(config.Server.DashboardEnabled);
            Assert.Equal(500, config.Cache.MaxSizeMb);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Load_EnvVarSubstitution_ResolvesVariable()
    {
        Environment.SetEnvironmentVariable("TEST_IMAP_PASSWORD", "secret123");

        var json = """
        {
          "accounts": [
            {
              "name": "test",
              "imap_host": "imap.test.com",
              "imap_port": 993,
              "username": "test@test.com",
              "auth_type": "password",
              "provider": "generic",
              "password": "${TEST_IMAP_PASSWORD}"
            }
          ]
        }
        """;

        var tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, json);

        try
        {
            var config = ConfigLoader.LoadFromFile(tmpFile);
            Assert.Equal("secret123", config.Accounts[0].Password);
        }
        finally
        {
            File.Delete(tmpFile);
            Environment.SetEnvironmentVariable("TEST_IMAP_PASSWORD", null);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: FAIL — `ConfigLoader` class does not exist.

- [ ] **Step 3: Create AppConfig model classes**

```csharp
// src/AwesomeImapMcp.Core/Configuration/AppConfig.cs
using System.Text.Json.Serialization;
using AwesomeImapMcp.Core.Types;

namespace AwesomeImapMcp.Core.Configuration;

public class AppConfig
{
    [JsonPropertyName("server")]
    public ServerConfig Server { get; set; } = new();

    [JsonPropertyName("accounts")]
    public List<AccountConfig> Accounts { get; set; } = [];

    [JsonPropertyName("cache")]
    public CacheConfig Cache { get; set; } = new();

    [JsonPropertyName("queue")]
    public QueueConfig Queue { get; set; } = new();

    [JsonPropertyName("llm")]
    public LlmConfig Llm { get; set; } = new();

    [JsonPropertyName("metrics")]
    public MetricsConfig Metrics { get; set; } = new();
}

public class ServerConfig
{
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = "stdio";

    [JsonPropertyName("http_port")]
    public int HttpPort { get; set; } = 3846;

    [JsonPropertyName("dashboard_port")]
    public int DashboardPort { get; set; } = 3847;

    [JsonPropertyName("dashboard_enabled")]
    public bool DashboardEnabled { get; set; } = true;

    [JsonPropertyName("dashboard_auth")]
    public string DashboardAuth { get; set; } = "pin";
}

public class AccountConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("imap_host")]
    public required string ImapHost { get; set; }

    [JsonPropertyName("imap_port")]
    public int ImapPort { get; set; } = 993;

    [JsonPropertyName("smtp_host")]
    public string? SmtpHost { get; set; }

    [JsonPropertyName("smtp_port")]
    public int SmtpPort { get; set; } = 465;

    [JsonPropertyName("smtp_use_ssl")]
    public bool SmtpUseSsl { get; set; } = true;

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("auth_type")]
    public required string AuthType { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "generic";

    [JsonPropertyName("confirm_mode")]
    public string ConfirmMode { get; set; } = "implicit";

    [JsonPropertyName("undo_window_seconds")]
    public int UndoWindowSeconds { get; set; } = 10;

    [JsonPropertyName("sync")]
    public SyncConfig Sync { get; set; } = new();
}

public class SyncConfig
{
    [JsonPropertyName("idle_folders")]
    public List<string> IdleFolders { get; set; } = ["INBOX"];

    [JsonPropertyName("poll_interval")]
    public int PollInterval { get; set; } = 300;

    [JsonPropertyName("folders")]
    public List<FolderSyncConfig>? Folders { get; set; }
}

public class FolderSyncConfig
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("cache_window_days")]
    public int CacheWindowDays { get; set; } = 0;
}

public class CacheConfig
{
    [JsonPropertyName("db_path")]
    public string DbPath { get; set; } = "~/.awesome-imap-mcp/cache.db";

    [JsonPropertyName("max_size_mb")]
    public int MaxSizeMb { get; set; } = 500;

    [JsonPropertyName("default_window_days")]
    public int DefaultWindowDays { get; set; } = 0;

    [JsonPropertyName("max_body_age_days")]
    public int MaxBodyAgeDays { get; set; } = 0;

    [JsonPropertyName("imap_fallback_ttl_hours")]
    public int ImapFallbackTtlHours { get; set; } = 1;
}

public class QueueConfig
{
    [JsonPropertyName("p0_flush_interval")]
    public int P0FlushInterval { get; set; } = 2;

    [JsonPropertyName("p1_flush_interval")]
    public int P1FlushInterval { get; set; } = 30;

    [JsonPropertyName("p2_flush_interval")]
    public int P2FlushInterval { get; set; } = 300;

    [JsonPropertyName("send_undo_window")]
    public int SendUndoWindow { get; set; } = 10;

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; } = 3;
}

public class LlmConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "anthropic";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    [JsonPropertyName("api_key_env")]
    public string ApiKeyEnv { get; set; } = "ANTHROPIC_API_KEY";

    [JsonPropertyName("daily_token_budget")]
    public int DailyTokenBudget { get; set; } = 1_000_000;

    [JsonPropertyName("monthly_cost_limit")]
    public decimal MonthlyCostLimit { get; set; } = 5.00m;

    [JsonPropertyName("auto_analyze_new")]
    public bool AutoAnalyzeNew { get; set; } = false;
}

public class MetricsConfig
{
    [JsonPropertyName("internal_retention_days")]
    public int InternalRetentionDays { get; set; } = 7;

    [JsonPropertyName("otlp_endpoint")]
    public string? OtlpEndpoint { get; set; }

    [JsonPropertyName("otlp_protocol")]
    public string OtlpProtocol { get; set; } = "grpc";

    [JsonPropertyName("export_interval_seconds")]
    public int ExportIntervalSeconds { get; set; } = 15;
}
```

- [ ] **Step 4: Create ConfigLoader**

```csharp
// src/AwesomeImapMcp.Core/Configuration/ConfigLoader.cs
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AwesomeImapMcp.Core.Configuration;

public static partial class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        var json = File.ReadAllText(path);
        json = SubstituteEnvVars(json);

        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize config");

        return config;
    }

    public static string ResolveDbPath(string dbPath)
    {
        if (dbPath.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, dbPath[2..]);
        }
        return dbPath;
    }

    private static string SubstituteEnvVars(string json)
    {
        return EnvVarPattern().Replace(json, match =>
        {
            var varName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName) ?? match.Value;
        });
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex EnvVarPattern();
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: All 4 ConfigLoader tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): add configuration model and JSON config loader with env var substitution"
```

---

## Chunk 2: Database + Encryption + Provider Profiles

### Task 4: Database and Migration Runner

**Files:**
- Create: `src/AwesomeImapMcp.Core/Database/Database.cs`
- Create: `src/AwesomeImapMcp.Core/Database/MigrationRunner.cs`
- Create: `tests/AwesomeImapMcp.Core.Tests/Database/DatabaseTests.cs`
- Create: `tests/AwesomeImapMcp.Core.Tests/Database/MigrationRunnerTests.cs`

- [ ] **Step 1: Write failing tests for Database**

```csharp
// tests/AwesomeImapMcp.Core.Tests/Database/DatabaseTests.cs
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Tests.Database;

public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
    }

    [Fact]
    public void Constructor_CreatesDatabase_WithWalMode()
    {
        using var conn = _db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var result = cmd.ExecuteScalar()?.ToString();
        Assert.Equal("wal", result);
    }

    [Fact]
    public void Constructor_CreatesDatabase_WithForeignKeys()
    {
        using var conn = _db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(1, result);
    }

    [Fact]
    public void GetWriteConnection_ReturnsSameConnection()
    {
        var conn1 = _db.GetWriteConnection();
        var conn2 = _db.GetWriteConnection();
        Assert.Same(conn1, conn2);
    }

    [Fact]
    public void GetReadConnection_ReturnsDifferentConnections()
    {
        using var conn1 = _db.GetReadConnection();
        using var conn2 = _db.GetReadConnection();
        Assert.NotSame(conn1, conn2);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
```

- [ ] **Step 2: Write failing tests for MigrationRunner**

```csharp
// tests/AwesomeImapMcp.Core.Tests/Database/MigrationRunnerTests.cs
using Microsoft.Data.Sqlite;
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Tests.Database;

public class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
    }

    [Fact]
    public void Migrate_CreatesSchemaVersionTable()
    {
        MigrationRunner.Migrate(_db);

        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.True(count >= 0);
    }

    [Fact]
    public void Migrate_AppliesMigrations_InOrder()
    {
        MigrationRunner.Migrate(_db);

        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version ORDER BY version;";
        using var reader = cmd.ExecuteReader();
        var versions = new List<int>();
        while (reader.Read())
            versions.Add(reader.GetInt32(0));

        Assert.Contains(1, versions);
    }

    [Fact]
    public void Migrate_Idempotent_RunningTwiceNoError()
    {
        MigrationRunner.Migrate(_db);
        MigrationRunner.Migrate(_db); // should not throw
    }

    [Fact]
    public void Migrate_CreatesAccountsTable()
    {
        MigrationRunner.Migrate(_db);

        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM accounts;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(0, count);
    }

    [Fact]
    public void Migrate_CreatesMessagesTable()
    {
        MigrationRunner.Migrate(_db);

        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(0, count);
    }

    [Fact]
    public void Migrate_CreatesFtsTable()
    {
        MigrationRunner.Migrate(_db);

        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages_fts;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(0, count);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: FAIL — `AppDatabase` class does not exist.

- [ ] **Step 4: Implement AppDatabase**

```csharp
// src/AwesomeImapMcp.Core/Database/Database.cs
using Microsoft.Data.Sqlite;

namespace AwesomeImapMcp.Core.Database;

public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string DbPath { get; }

    public AppDatabase(string dbPath)
    {
        DbPath = dbPath;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();

        _writeConnection = new SqliteConnection(connStr);
        _writeConnection.Open();

        // Enable WAL mode
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the single write connection. Thread-safe: callers share one connection.
    /// For write serialization across threads, use <see cref="AcquireWriteLockAsync"/>.
    /// </summary>
    public SqliteConnection GetWriteConnection() => _writeConnection;

    /// <summary>Acquire exclusive write lock for multi-statement transactions.</summary>
    public async Task<IDisposable> AcquireWriteLockAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        return new WriteLockRelease(_writeLock);
    }

    public SqliteConnection GetReadConnection()
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();

        return new SqliteConnection(connStr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeConnection.Dispose();
        _writeLock.Dispose();
    }

    private sealed class WriteLockRelease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
```

- [ ] **Step 5: Implement MigrationRunner**

```csharp
// src/AwesomeImapMcp.Core/Database/MigrationRunner.cs
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AwesomeImapMcp.Core.Database;

public static class MigrationRunner
{
    public static void Migrate(AppDatabase db)
    {
        var conn = db.GetWriteConnection();

        EnsureSchemaVersionTable(conn);
        var applied = GetAppliedVersions(conn);
        var migrations = LoadMigrations();

        foreach (var (version, sql) in migrations.OrderBy(m => m.Version))
        {
            if (applied.Contains(version))
                continue;

            using var transaction = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText =
                    "INSERT INTO schema_version (version, applied_at) VALUES ($version, datetime('now'));";
                insertCmd.Parameters.AddWithValue("$version", version);
                insertCmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static void EnsureSchemaVersionTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static HashSet<int> GetAppliedVersions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version;";
        using var reader = cmd.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static List<(int Version, string Sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "AwesomeImapMcp.Core.Database.Migrations.";
        var migrations = new List<(int Version, string Sql)>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".sql")))
        {
            var fileName = resourceName[prefix.Length..];
            var versionStr = fileName.Split('_')[0];
            if (int.TryParse(versionStr, out var version))
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                migrations.Add((version, reader.ReadToEnd()));
            }
        }

        return migrations;
    }
}
```

- [ ] **Step 6: Run tests to verify they fail on missing migration**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: Database tests pass, MigrationRunner tests fail on missing migration file (no tables created).

---

### Task 5: Initial Migration

**Files:**
- Create: `src/AwesomeImapMcp.Core/Database/Migrations/001_initial.sql`
- Modify: `src/AwesomeImapMcp.Core/AwesomeImapMcp.Core.csproj` (embed migration as resource)

- [ ] **Step 1: Add EmbeddedResource to csproj**

Add this to `src/AwesomeImapMcp.Core/AwesomeImapMcp.Core.csproj` inside `<Project>`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Database\Migrations\*.sql" />
</ItemGroup>
```

- [ ] **Step 2: Create 001_initial.sql**

This contains all Phase 1 tables from DATA_MODEL.md: accounts, folders, messages, messages_fts (with triggers), attachments.

```sql
-- src/AwesomeImapMcp.Core/Database/Migrations/001_initial.sql
-- Phase 1 tables: accounts, folders, messages, FTS, attachments

CREATE TABLE accounts (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    imap_host       TEXT NOT NULL,
    imap_port       INTEGER NOT NULL DEFAULT 993,
    smtp_host       TEXT,
    smtp_port       INTEGER DEFAULT 465,
    smtp_use_ssl    INTEGER DEFAULT 1,
    username        TEXT NOT NULL,
    auth_type       TEXT NOT NULL,
    credentials_enc TEXT NOT NULL,
    provider        TEXT NOT NULL DEFAULT 'generic',
    config_json     TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE folders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    path            TEXT NOT NULL,
    display_name    TEXT,
    role            TEXT,
    delimiter       TEXT DEFAULT '/',
    flags           TEXT,
    message_count   INTEGER DEFAULT 0,
    unread_count    INTEGER DEFAULT 0,
    last_synced_uid INTEGER DEFAULT 0,
    last_synced_at  TEXT,
    sync_enabled    INTEGER DEFAULT 1,
    idle_enabled    INTEGER DEFAULT 0,
    poll_interval   INTEGER DEFAULT 300,
    UNIQUE(account_id, path)
);
CREATE INDEX idx_folders_account ON folders(account_id);
CREATE INDEX idx_folders_role ON folders(account_id, role);

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    folder_id       INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    uid             INTEGER NOT NULL,
    message_id      TEXT,
    in_reply_to     TEXT,
    references_hdr  TEXT,
    thread_id       TEXT,
    subject         TEXT,
    from_address    TEXT,
    from_email      TEXT,
    to_addresses    TEXT,
    cc_addresses    TEXT,
    bcc_addresses   TEXT,
    date            TEXT NOT NULL,
    date_epoch      INTEGER,
    flags           TEXT,
    size_bytes      INTEGER,
    has_attachments INTEGER DEFAULT 0,
    body_text       TEXT,
    body_html       TEXT,
    body_fetched    INTEGER DEFAULT 0,
    snippet         TEXT,
    raw_headers     TEXT,
    cached_at       TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(account_id, folder_id, uid)
);
CREATE INDEX idx_messages_account_folder ON messages(account_id, folder_id);
CREATE INDEX idx_messages_date ON messages(date_epoch DESC);
CREATE INDEX idx_messages_from ON messages(from_email);
CREATE INDEX idx_messages_thread ON messages(thread_id);
CREATE INDEX idx_messages_message_id ON messages(message_id);
CREATE INDEX idx_messages_flags ON messages(account_id, folder_id, flags);
CREATE INDEX idx_messages_has_attachments ON messages(has_attachments) WHERE has_attachments = 1;
CREATE INDEX idx_messages_unread ON messages(account_id, folder_id) WHERE flags NOT LIKE '%\Seen%';

CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject,
    body_text,
    from_address,
    snippet,
    content='messages',
    content_rowid='id',
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TRIGGER messages_fts_insert AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;

CREATE TRIGGER messages_fts_delete BEFORE DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
END;

CREATE TRIGGER messages_fts_update AFTER UPDATE OF subject, body_text, from_address, snippet ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, body_text, from_address, snippet)
    VALUES ('delete', old.id, old.subject, old.body_text, old.from_address, old.snippet);
    INSERT INTO messages_fts(rowid, subject, body_text, from_address, snippet)
    VALUES (new.id, new.subject, new.body_text, new.from_address, new.snippet);
END;

CREATE TABLE attachments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    filename        TEXT,
    content_type    TEXT,
    size_bytes      INTEGER,
    content_id      TEXT,
    is_inline       INTEGER DEFAULT 0,
    local_path      TEXT,
    downloaded_at   TEXT
);
CREATE INDEX idx_attachments_message ON attachments(message_id);
```

- [ ] **Step 3: Run all tests**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: All Database and MigrationRunner tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(core): add SQLite database with WAL mode, migration runner, and initial schema"
```

---

### Task 6: Encryption

**Files:**
- Create: `src/AwesomeImapMcp.Core/Encryption/CredentialEncryptor.cs`
- Create: `src/AwesomeImapMcp.Core/Encryption/MachineId.cs`
- Create: `tests/AwesomeImapMcp.Core.Tests/Encryption/CredentialEncryptorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AwesomeImapMcp.Core.Tests/Encryption/CredentialEncryptorTests.cs
using AwesomeImapMcp.Core.Encryption;

namespace AwesomeImapMcp.Core.Tests.Encryption;

public class CredentialEncryptorTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginal()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var plaintext = """{"password":"s3cret","token":"abc123"}""";

        var encrypted = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutput_EachCall()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");
        var plaintext = "same input";

        var encrypted1 = encryptor.Encrypt(plaintext);
        var encrypted2 = encryptor.Encrypt(plaintext);

        Assert.NotEqual(encrypted1, encrypted2); // different nonce each time
    }

    [Fact]
    public void Decrypt_WrongPassphrase_Throws()
    {
        var encryptor1 = new CredentialEncryptor("passphrase-1");
        var encryptor2 = new CredentialEncryptor("passphrase-2");

        var encrypted = encryptor1.Encrypt("secret data");

        Assert.ThrowsAny<Exception>(() => encryptor2.Decrypt(encrypted));
    }

    [Fact]
    public void Encrypt_EmptyString_Works()
    {
        var encryptor = new CredentialEncryptor("test-passphrase");

        var encrypted = encryptor.Encrypt("");
        var decrypted = encryptor.Decrypt(encrypted);

        Assert.Equal("", decrypted);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: FAIL — `CredentialEncryptor` class does not exist.

- [ ] **Step 3: Implement MachineId**

```csharp
// src/AwesomeImapMcp.Core/Encryption/MachineId.cs
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AwesomeImapMcp.Core.Encryption;

public static class MachineId
{
    public static string Get()
    {
        string raw;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid
            raw = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", "")?.ToString() ?? Environment.MachineName;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var ioregOutput = RunCommand("ioreg", "-rd1 -c IOPlatformExpertDevice");
            raw = ExtractMacUuid(ioregOutput) ?? Environment.MachineName;
        }
        else // Linux
        {
            raw = ReadFileOrDefault("/etc/machine-id")
                ?? ReadFileOrDefault("/var/lib/dbus/machine-id")
                ?? Environment.MachineName;
        }

        // Hash to normalize length
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractMacUuid(string? ioregOutput)
    {
        if (ioregOutput == null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            ioregOutput, @"""IOPlatformUUID""\s*=\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadFileOrDefault(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }
    }

    private static string? RunCommand(string command, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            return process?.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }
}
```

- [ ] **Step 4: Implement CredentialEncryptor**

```csharp
// src/AwesomeImapMcp.Core/Encryption/CredentialEncryptor.cs
using System.Security.Cryptography;
using System.Text;

namespace AwesomeImapMcp.Core.Encryption;

public sealed class CredentialEncryptor
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 100_000;

    private readonly string _passphrase;

    public CredentialEncryptor(string passphrase)
    {
        _passphrase = passphrase;
    }

    /// <summary>
    /// Creates a CredentialEncryptor using the machine ID as the passphrase.
    /// Warns that credentials will not be portable.
    /// </summary>
    public static CredentialEncryptor FromMachineId()
    {
        return new CredentialEncryptor(MachineId.Get());
    }

    public string Encrypt(string plaintext)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: salt + nonce + tag + ciphertext, base64 encoded
        var combined = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        salt.CopyTo(combined, 0);
        nonce.CopyTo(combined, SaltSize);
        tag.CopyTo(combined, SaltSize + NonceSize);
        ciphertext.CopyTo(combined, SaltSize + NonceSize + TagSize);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string encryptedBase64)
    {
        var combined = Convert.FromBase64String(encryptedBase64);

        var salt = combined[..SaltSize];
        var nonce = combined[SaltSize..(SaltSize + NonceSize)];
        var tag = combined[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
        var ciphertext = combined[(SaltSize + NonceSize + TagSize)..];

        var key = DeriveKey(salt);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] DeriveKey(byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            _passphrase, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: All encryption tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): add AES-256-GCM credential encryption with PBKDF2 key derivation"
```

---

### Task 7: Provider Profile Registry

**Files:**
- Create: `src/AwesomeImapMcp.Core/Providers/ProviderProfileRegistry.cs`
- Create: `tests/AwesomeImapMcp.Core.Tests/Providers/ProviderProfileRegistryTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AwesomeImapMcp.Core.Tests/Providers/ProviderProfileRegistryTests.cs
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Types;

namespace AwesomeImapMcp.Core.Tests.Providers;

public class ProviderProfileRegistryTests
{
    private readonly ProviderProfileRegistry _registry = new();

    [Theory]
    [InlineData("imap.gmail.com", ProviderType.Gmail)]
    [InlineData("imap.googlemail.com", ProviderType.Gmail)]
    [InlineData("outlook.office365.com", ProviderType.Outlook)]
    [InlineData("imap.fastmail.com", ProviderType.Fastmail)]
    [InlineData("imap.mail.yahoo.com", ProviderType.Yahoo)]
    [InlineData("127.0.0.1", ProviderType.Generic)]
    [InlineData("imap.example.com", ProviderType.Generic)]
    public void DetectFromHost_ReturnsCorrectProvider(string host, ProviderType expected)
    {
        var profile = _registry.DetectFromHost(host);
        Assert.Equal(expected, profile.Type);
    }

    [Fact]
    public void GetProfile_Gmail_HasCorrectFolderMap()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        Assert.Equal("[Gmail]/Sent Mail", profile.FolderMap[FolderRole.Sent]);
        Assert.Equal("[Gmail]/Trash", profile.FolderMap[FolderRole.Trash]);
        Assert.Equal("[Gmail]/Drafts", profile.FolderMap[FolderRole.Drafts]);
    }

    [Fact]
    public void GetProfile_Outlook_HasCorrectFolderMap()
    {
        var profile = _registry.GetProfile(ProviderType.Outlook);
        Assert.Equal("Sent Items", profile.FolderMap[FolderRole.Sent]);
        Assert.Equal("Deleted Items", profile.FolderMap[FolderRole.Trash]);
    }

    [Fact]
    public void GetProfile_AllProviders_HaveInbox()
    {
        foreach (var type in Enum.GetValues<ProviderType>())
        {
            var profile = _registry.GetProfile(type);
            Assert.Equal("INBOX", profile.FolderMap[FolderRole.Inbox]);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: FAIL — `ProviderProfileRegistry` class does not exist.

- [ ] **Step 3: Implement ProviderProfileRegistry**

```csharp
// src/AwesomeImapMcp.Core/Providers/ProviderProfileRegistry.cs
using AwesomeImapMcp.Core.Types;

namespace AwesomeImapMcp.Core.Providers;

public class ProviderProfileRegistry
{
    private readonly Dictionary<ProviderType, ProviderProfile> _profiles;

    public ProviderProfileRegistry()
    {
        _profiles = new Dictionary<ProviderType, ProviderProfile>
        {
            [ProviderType.Gmail] = new()
            {
                Type = ProviderType.Gmail,
                Name = "Gmail",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "[Gmail]/Sent Mail",
                    [FolderRole.Drafts] = "[Gmail]/Drafts",
                    [FolderRole.Trash] = "[Gmail]/Trash",
                    [FolderRole.Spam] = "[Gmail]/Spam",
                    [FolderRole.Archive] = "[Gmail]/All Mail"
                },
                SupportedAuth = [AuthMethod.OAuth2, AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 5
            },
            [ProviderType.Outlook] = new()
            {
                Type = ProviderType.Outlook,
                Name = "Outlook / Office 365",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "Sent Items",
                    [FolderRole.Drafts] = "Drafts",
                    [FolderRole.Trash] = "Deleted Items",
                    [FolderRole.Spam] = "Junk Email",
                    [FolderRole.Archive] = "Archive"
                },
                SupportedAuth = [AuthMethod.OAuth2],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 4
            },
            [ProviderType.Fastmail] = new()
            {
                Type = ProviderType.Fastmail,
                Name = "Fastmail",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "Sent",
                    [FolderRole.Drafts] = "Drafts",
                    [FolderRole.Trash] = "Trash",
                    [FolderRole.Spam] = "Junk Mail",
                    [FolderRole.Archive] = "Archive"
                },
                SupportedAuth = [AuthMethod.Password, AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch
                    | SearchCapabilities.FuzzySearch | SearchCapabilities.SortExtension,
                MaxConnections = 5
            },
            [ProviderType.ProtonMail] = new()
            {
                Type = ProviderType.ProtonMail,
                Name = "ProtonMail Bridge",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "Sent",
                    [FolderRole.Drafts] = "Drafts",
                    [FolderRole.Trash] = "Trash",
                    [FolderRole.Spam] = "Spam",
                    [FolderRole.Archive] = "Archive"
                },
                SupportedAuth = [AuthMethod.Password],
                Search = SearchCapabilities.BasicSearch,
                MaxConnections = 3,
                RequiresTlsTrust = true
            },
            [ProviderType.Yahoo] = new()
            {
                Type = ProviderType.Yahoo,
                Name = "Yahoo Mail",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "Sent",
                    [FolderRole.Drafts] = "Draft",
                    [FolderRole.Trash] = "Trash",
                    [FolderRole.Spam] = "Bulk Mail",
                    [FolderRole.Archive] = "Archive"
                },
                SupportedAuth = [AuthMethod.AppPassword],
                Search = SearchCapabilities.BasicSearch | SearchCapabilities.BodySearch,
                MaxConnections = 3
            },
            [ProviderType.Generic] = new()
            {
                Type = ProviderType.Generic,
                Name = "Generic IMAP",
                FolderMap = new()
                {
                    [FolderRole.Inbox] = "INBOX",
                    [FolderRole.Sent] = "Sent",
                    [FolderRole.Drafts] = "Drafts",
                    [FolderRole.Trash] = "Trash",
                    [FolderRole.Spam] = "Spam",
                    [FolderRole.Archive] = "Archive"
                },
                SupportedAuth = [AuthMethod.Password, AuthMethod.AppPassword, AuthMethod.OAuth2],
                Search = SearchCapabilities.BasicSearch
            }
        };
    }

    public ProviderProfile GetProfile(ProviderType type) => _profiles[type];

    public ProviderProfile DetectFromHost(string host)
    {
        var h = host.ToLowerInvariant();

        if (h.Contains("gmail") || h.Contains("googlemail"))
            return _profiles[ProviderType.Gmail];
        if (h.Contains("outlook") || h.Contains("office365") || h.Contains("hotmail"))
            return _profiles[ProviderType.Outlook];
        if (h.Contains("fastmail"))
            return _profiles[ProviderType.Fastmail];
        if (h.Contains("protonmail"))
            return _profiles[ProviderType.ProtonMail];
        if (h.Contains("yahoo") || h.Contains("aol"))
            return _profiles[ProviderType.Yahoo];

        return _profiles[ProviderType.Generic];
    }

    public ProviderProfile GetProfileByName(string providerName)
    {
        if (Enum.TryParse<ProviderType>(providerName, ignoreCase: true, out var type))
            return _profiles[type];
        return _profiles[ProviderType.Generic];
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/AwesomeImapMcp.Core.Tests
```

Expected: All provider tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add provider profiles with auto-detection for Gmail, Outlook, Fastmail, ProtonMail, Yahoo"
```

---

## Chunk 3: Repositories + IMAP Client Components

### Task 8: Repositories

**Files:**
- Create: `src/AwesomeImapMcp.ImapClient/Repositories/AccountRepository.cs`
- Create: `src/AwesomeImapMcp.ImapClient/Repositories/FolderRepository.cs`
- Create: `src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs`
- Create: `src/AwesomeImapMcp.ImapClient/Repositories/AttachmentRepository.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/Repositories/AccountRepositoryTests.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/Repositories/FolderRepositoryTests.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/Repositories/MessageRepositoryTests.cs`

- [ ] **Step 1: Write failing tests for AccountRepository**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/Repositories/AccountRepositoryTests.cs
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

public class AccountRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _repo;

    public AccountRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _repo = new AccountRepository(_db);
    }

    [Fact]
    public void Insert_And_GetById_ReturnsAccount()
    {
        _repo.Insert("personal", "Personal Gmail", "imap.gmail.com", 993,
            "smtp.gmail.com", 465, true, "test@gmail.com", "app_password",
            "encrypted_creds", "gmail", null);

        var account = _repo.GetById("personal");
        Assert.NotNull(account);
        Assert.Equal("Personal Gmail", account.Name);
        Assert.Equal("imap.gmail.com", account.ImapHost);
        Assert.Equal("test@gmail.com", account.Username);
    }

    [Fact]
    public void GetAll_ReturnsAllAccounts()
    {
        _repo.Insert("a1", "Account 1", "imap.a.com", 993,
            null, 465, true, "u1@a.com", "password", "enc1", "generic", null);
        _repo.Insert("a2", "Account 2", "imap.b.com", 993,
            null, 465, true, "u2@b.com", "password", "enc2", "generic", null);

        var all = _repo.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetById_NonExistent_ReturnsNull()
    {
        var account = _repo.GetById("nonexistent");
        Assert.Null(account);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
```

- [ ] **Step 2: Write failing tests for FolderRepository**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/Repositories/FolderRepositoryTests.cs
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

public class FolderRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly FolderRepository _repo;

    public FolderRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        var accountRepo = new AccountRepository(_db);
        accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 465, true, "u@test.com", "password", "enc", "generic", null);
        _repo = new FolderRepository(_db);
    }

    [Fact]
    public void Insert_And_GetByPath_ReturnsFolder()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        var folder = _repo.GetByPath("test", "INBOX");
        Assert.NotNull(folder);
        Assert.Equal("Inbox", folder.DisplayName);
        Assert.Equal("inbox", folder.Role);
    }

    [Fact]
    public void GetByAccount_ReturnsAllFolders()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        _repo.Insert("test", "Sent", "Sent", "sent", "/");
        var folders = _repo.GetByAccount("test");
        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void Insert_Duplicate_IgnoredSilently()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/"); // should not throw
        var folders = _repo.GetByAccount("test");
        Assert.Single(folders);
    }

    [Fact]
    public void UpdateSyncState_UpdatesFields()
    {
        _repo.Insert("test", "INBOX", "Inbox", "inbox", "/");
        var folder = _repo.GetByPath("test", "INBOX")!;
        _repo.UpdateSyncState(folder.Id, lastSyncedUid: 42, messageCount: 100, unreadCount: 5);

        var updated = _repo.GetByPath("test", "INBOX")!;
        Assert.Equal(42, updated.LastSyncedUid);
        Assert.Equal(100, updated.MessageCount);
        Assert.Equal(5, updated.UnreadCount);
        Assert.NotNull(updated.LastSyncedAt);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
```

- [ ] **Step 3: Write failing tests for MessageRepository (including FTS)**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/Repositories/MessageRepositoryTests.cs
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient.Tests.Repositories;

public class MessageRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;

    public MessageRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        _accountRepo = new AccountRepository(_db);
        _folderRepo = new FolderRepository(_db);
        _messageRepo = new MessageRepository(_db);

        // Seed test data
        _accountRepo.Insert("test", "Test", "imap.test.com", 993,
            null, 465, true, "u@test.com", "password", "enc", "generic", null);
        _folderRepo.Insert("test", "INBOX", "Inbox", "inbox", "/");
    }

    [Fact]
    public void Insert_And_GetByUid_ReturnsMessage()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "thread1",
            subject: "Hello World", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[\"bob@test.com\"]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[\"\\\\Seen\"]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Hello, this is a test email");

        var msg = _messageRepo.GetByUid("test", folderId, 1);
        Assert.NotNull(msg);
        Assert.Equal("Hello World", msg.Subject);
        Assert.Equal("alice@test.com", msg.FromEmail);
    }

    [Fact]
    public void SearchFts_FindsBySubject()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t1",
            subject: "Meeting tomorrow at 3pm", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "Let's meet tomorrow");

        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "t2",
            subject: "Invoice #1234", fromAddress: "Bob <bob@test.com>",
            fromEmail: "bob@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T11:00:00Z",
            dateEpoch: 1774044000, flags: "[]", sizeBytes: 2048,
            hasAttachments: true, snippet: "Please find attached invoice");

        var results = _messageRepo.SearchFts("meeting", accountId: "test", maxResults: 10);
        Assert.Single(results);
        Assert.Equal("Meeting tomorrow at 3pm", results[0].Subject);
    }

    [Fact]
    public void GetByThreadId_ReturnsThreadMessages()
    {
        var folderId = _folderRepo.GetByPath("test", "INBOX")!.Id;
        _messageRepo.Insert("test", folderId, uid: 1, messageId: "<msg1@test.com>",
            inReplyTo: null, referencesHdr: null, threadId: "thread-abc",
            subject: "Original", fromAddress: "Alice <alice@test.com>",
            fromEmail: "alice@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T10:00:00Z",
            dateEpoch: 1774040400, flags: "[]", sizeBytes: 512,
            hasAttachments: false, snippet: "First message");

        _messageRepo.Insert("test", folderId, uid: 2, messageId: "<msg2@test.com>",
            inReplyTo: "<msg1@test.com>", referencesHdr: "<msg1@test.com>",
            threadId: "thread-abc",
            subject: "Re: Original", fromAddress: "Bob <bob@test.com>",
            fromEmail: "bob@test.com", toAddresses: "[]",
            ccAddresses: null, bccAddresses: null, date: "2026-03-18T11:00:00Z",
            dateEpoch: 1774044000, flags: "[]", sizeBytes: 1024,
            hasAttachments: false, snippet: "Reply here");

        var thread = _messageRepo.GetByThreadId("thread-abc");
        Assert.Equal(2, thread.Count);
        Assert.Equal("Original", thread[0].Subject);
        Assert.Equal("Re: Original", thread[1].Subject);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.ImapClient.Tests
```

Expected: FAIL — repository classes do not exist.

- [ ] **Step 4: Implement AccountRepository**

```csharp
// src/AwesomeImapMcp.ImapClient/Repositories/AccountRepository.cs
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.ImapClient.Repositories;

public record AccountRecord(
    string Id, string Name, string ImapHost, int ImapPort,
    string? SmtpHost, int SmtpPort, bool SmtpUseSsl,
    string Username, string AuthType, string CredentialsEnc,
    string Provider, string? ConfigJson,
    string CreatedAt, string UpdatedAt);

public class AccountRepository(AppDatabase db)
{
    public void Insert(string id, string name, string imapHost, int imapPort,
        string? smtpHost, int smtpPort, bool smtpUseSsl, string username,
        string authType, string credentialsEnc, string provider, string? configJson)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (id, name, imap_host, imap_port, smtp_host, smtp_port,
                smtp_use_ssl, username, auth_type, credentials_enc, provider, config_json)
            VALUES ($id, $name, $imapHost, $imapPort, $smtpHost, $smtpPort,
                $smtpUseSsl, $username, $authType, $credentialsEnc, $provider, $configJson);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$imapHost", imapHost);
        cmd.Parameters.AddWithValue("$imapPort", imapPort);
        cmd.Parameters.AddWithValue("$smtpHost", (object?)smtpHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smtpPort", smtpPort);
        cmd.Parameters.AddWithValue("$smtpUseSsl", smtpUseSsl ? 1 : 0);
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$authType", authType);
        cmd.Parameters.AddWithValue("$credentialsEnc", credentialsEnc);
        cmd.Parameters.AddWithValue("$provider", provider);
        cmd.Parameters.AddWithValue("$configJson", (object?)configJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public AccountRecord? GetById(string id)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<AccountRecord> GetAll()
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<AccountRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    private static AccountRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetString(r.GetOrdinal("id")),
        Name: r.GetString(r.GetOrdinal("name")),
        ImapHost: r.GetString(r.GetOrdinal("imap_host")),
        ImapPort: r.GetInt32(r.GetOrdinal("imap_port")),
        SmtpHost: r.IsDBNull(r.GetOrdinal("smtp_host")) ? null : r.GetString(r.GetOrdinal("smtp_host")),
        SmtpPort: r.GetInt32(r.GetOrdinal("smtp_port")),
        SmtpUseSsl: r.GetInt32(r.GetOrdinal("smtp_use_ssl")) == 1,
        Username: r.GetString(r.GetOrdinal("username")),
        AuthType: r.GetString(r.GetOrdinal("auth_type")),
        CredentialsEnc: r.GetString(r.GetOrdinal("credentials_enc")),
        Provider: r.GetString(r.GetOrdinal("provider")),
        ConfigJson: r.IsDBNull(r.GetOrdinal("config_json")) ? null : r.GetString(r.GetOrdinal("config_json")),
        CreatedAt: r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt: r.GetString(r.GetOrdinal("updated_at"))
    );
}
```

- [ ] **Step 5: Implement FolderRepository**

```csharp
// src/AwesomeImapMcp.ImapClient/Repositories/FolderRepository.cs
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.ImapClient.Repositories;

public record FolderRecord(
    int Id, string AccountId, string Path, string? DisplayName,
    string? Role, string Delimiter, string? Flags,
    int MessageCount, int UnreadCount, int LastSyncedUid,
    string? LastSyncedAt, bool SyncEnabled, bool IdleEnabled, int PollInterval);

public class FolderRepository(AppDatabase db)
{
    public void Insert(string accountId, string path, string? displayName,
        string? role, string delimiter)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO folders (account_id, path, display_name, role, delimiter)
            VALUES ($accountId, $path, $displayName, $role, $delimiter);
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$displayName", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$delimiter", delimiter);
        cmd.ExecuteNonQuery();
    }

    public FolderRecord? GetByPath(string accountId, string path)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM folders WHERE account_id = $accountId AND path = $path;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<FolderRecord> GetByAccount(string accountId)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM folders WHERE account_id = $accountId ORDER BY path;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var list = new List<FolderRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateSyncState(int folderId, int lastSyncedUid, int messageCount, int unreadCount)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE folders SET last_synced_uid = $uid, message_count = $msgCount,
                unread_count = $unreadCount, last_synced_at = datetime('now')
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$uid", lastSyncedUid);
        cmd.Parameters.AddWithValue("$msgCount", messageCount);
        cmd.Parameters.AddWithValue("$unreadCount", unreadCount);
        cmd.ExecuteNonQuery();
    }

    private static FolderRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        Path: r.GetString(r.GetOrdinal("path")),
        DisplayName: r.IsDBNull(r.GetOrdinal("display_name")) ? null : r.GetString(r.GetOrdinal("display_name")),
        Role: r.IsDBNull(r.GetOrdinal("role")) ? null : r.GetString(r.GetOrdinal("role")),
        Delimiter: r.GetString(r.GetOrdinal("delimiter")),
        Flags: r.IsDBNull(r.GetOrdinal("flags")) ? null : r.GetString(r.GetOrdinal("flags")),
        MessageCount: r.GetInt32(r.GetOrdinal("message_count")),
        UnreadCount: r.GetInt32(r.GetOrdinal("unread_count")),
        LastSyncedUid: r.GetInt32(r.GetOrdinal("last_synced_uid")),
        LastSyncedAt: r.IsDBNull(r.GetOrdinal("last_synced_at")) ? null : r.GetString(r.GetOrdinal("last_synced_at")),
        SyncEnabled: r.GetInt32(r.GetOrdinal("sync_enabled")) == 1,
        IdleEnabled: r.GetInt32(r.GetOrdinal("idle_enabled")) == 1,
        PollInterval: r.GetInt32(r.GetOrdinal("poll_interval"))
    );
}
```

- [ ] **Step 6: Implement MessageRepository**

```csharp
// src/AwesomeImapMcp.ImapClient/Repositories/MessageRepository.cs
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.ImapClient.Repositories;

public record MessageRecord(
    int Id, string AccountId, int FolderId, int Uid,
    string? MessageId, string? InReplyTo, string? ReferencesHdr, string? ThreadId,
    string? Subject, string? FromAddress, string? FromEmail,
    string? ToAddresses, string? CcAddresses, string? BccAddresses,
    string Date, long? DateEpoch, string? Flags, int? SizeBytes,
    bool HasAttachments, string? BodyText, string? BodyHtml,
    bool BodyFetched, string? Snippet, string? RawHeaders, string CachedAt);

public class MessageRepository(AppDatabase db)
{
    public void Insert(string accountId, int folderId, int uid, string? messageId,
        string? inReplyTo, string? referencesHdr, string? threadId,
        string? subject, string? fromAddress, string? fromEmail,
        string? toAddresses, string? ccAddresses, string? bccAddresses,
        string date, long? dateEpoch, string? flags, int? sizeBytes,
        bool hasAttachments, string? snippet,
        string? bodyText = null, string? bodyHtml = null, string? rawHeaders = null)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages (account_id, folder_id, uid, message_id,
                in_reply_to, references_hdr, thread_id, subject, from_address, from_email,
                to_addresses, cc_addresses, bcc_addresses, date, date_epoch, flags,
                size_bytes, has_attachments, body_text, body_html, body_fetched, snippet, raw_headers)
            VALUES ($accountId, $folderId, $uid, $messageId,
                $inReplyTo, $referencesHdr, $threadId, $subject, $fromAddress, $fromEmail,
                $toAddresses, $ccAddresses, $bccAddresses, $date, $dateEpoch, $flags,
                $sizeBytes, $hasAttachments, $bodyText, $bodyHtml, $bodyFetched, $snippet, $rawHeaders);
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$folderId", folderId);
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inReplyTo", (object?)inReplyTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$referencesHdr", (object?)referencesHdr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$threadId", (object?)threadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fromAddress", (object?)fromAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fromEmail", (object?)fromEmail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$toAddresses", (object?)toAddresses ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccAddresses", (object?)ccAddresses ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bccAddresses", (object?)bccAddresses ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$dateEpoch", (object?)dateEpoch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flags", (object?)flags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sizeBytes", (object?)sizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hasAttachments", hasAttachments ? 1 : 0);
        cmd.Parameters.AddWithValue("$bodyText", (object?)bodyText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bodyHtml", (object?)bodyHtml ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bodyFetched", (bodyText != null || bodyHtml != null) ? 1 : 0);
        cmd.Parameters.AddWithValue("$snippet", (object?)snippet ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rawHeaders", (object?)rawHeaders ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public MessageRecord? GetByUid(string accountId, int folderId, int uid)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account_id = $a AND folder_id = $f AND uid = $u;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$u", uid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<MessageRecord> SearchFts(string query, string? accountId = null,
        int? folderId = null, int maxResults = 20)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();

        var where = "WHERE messages_fts MATCH $query";
        if (accountId != null) where += " AND m.account_id = $accountId";
        if (folderId != null) where += " AND m.folder_id = $folderId";

        cmd.CommandText = $"""
            SELECT m.* FROM messages m
            JOIN messages_fts ON messages_fts.rowid = m.id
            {where}
            ORDER BY m.date_epoch DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", query);
        if (accountId != null) cmd.Parameters.AddWithValue("$accountId", accountId);
        if (folderId != null) cmd.Parameters.AddWithValue("$folderId", folderId);
        cmd.Parameters.AddWithValue("$limit", maxResults);

        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public List<MessageRecord> GetByThreadId(string threadId)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE thread_id = $threadId ORDER BY date_epoch;";
        cmd.Parameters.AddWithValue("$threadId", threadId);
        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateBody(int messageId, string? bodyText, string? bodyHtml)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET body_text = $bodyText, body_html = $bodyHtml,
                body_fetched = 1 WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$bodyText", (object?)bodyText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bodyHtml", (object?)bodyHtml ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public int GetMaxUid(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(uid), 0) FROM messages
            WHERE account_id = $a AND folder_id = $f;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static MessageRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        FolderId: r.GetInt32(r.GetOrdinal("folder_id")),
        Uid: r.GetInt32(r.GetOrdinal("uid")),
        MessageId: r.IsDBNull(r.GetOrdinal("message_id")) ? null : r.GetString(r.GetOrdinal("message_id")),
        InReplyTo: r.IsDBNull(r.GetOrdinal("in_reply_to")) ? null : r.GetString(r.GetOrdinal("in_reply_to")),
        ReferencesHdr: r.IsDBNull(r.GetOrdinal("references_hdr")) ? null : r.GetString(r.GetOrdinal("references_hdr")),
        ThreadId: r.IsDBNull(r.GetOrdinal("thread_id")) ? null : r.GetString(r.GetOrdinal("thread_id")),
        Subject: r.IsDBNull(r.GetOrdinal("subject")) ? null : r.GetString(r.GetOrdinal("subject")),
        FromAddress: r.IsDBNull(r.GetOrdinal("from_address")) ? null : r.GetString(r.GetOrdinal("from_address")),
        FromEmail: r.IsDBNull(r.GetOrdinal("from_email")) ? null : r.GetString(r.GetOrdinal("from_email")),
        ToAddresses: r.IsDBNull(r.GetOrdinal("to_addresses")) ? null : r.GetString(r.GetOrdinal("to_addresses")),
        CcAddresses: r.IsDBNull(r.GetOrdinal("cc_addresses")) ? null : r.GetString(r.GetOrdinal("cc_addresses")),
        BccAddresses: r.IsDBNull(r.GetOrdinal("bcc_addresses")) ? null : r.GetString(r.GetOrdinal("bcc_addresses")),
        Date: r.GetString(r.GetOrdinal("date")),
        DateEpoch: r.IsDBNull(r.GetOrdinal("date_epoch")) ? null : r.GetInt64(r.GetOrdinal("date_epoch")),
        Flags: r.IsDBNull(r.GetOrdinal("flags")) ? null : r.GetString(r.GetOrdinal("flags")),
        SizeBytes: r.IsDBNull(r.GetOrdinal("size_bytes")) ? null : r.GetInt32(r.GetOrdinal("size_bytes")),
        HasAttachments: r.GetInt32(r.GetOrdinal("has_attachments")) == 1,
        BodyText: r.IsDBNull(r.GetOrdinal("body_text")) ? null : r.GetString(r.GetOrdinal("body_text")),
        BodyHtml: r.IsDBNull(r.GetOrdinal("body_html")) ? null : r.GetString(r.GetOrdinal("body_html")),
        BodyFetched: r.GetInt32(r.GetOrdinal("body_fetched")) == 1,
        Snippet: r.IsDBNull(r.GetOrdinal("snippet")) ? null : r.GetString(r.GetOrdinal("snippet")),
        RawHeaders: r.IsDBNull(r.GetOrdinal("raw_headers")) ? null : r.GetString(r.GetOrdinal("raw_headers")),
        CachedAt: r.GetString(r.GetOrdinal("cached_at"))
    );
}
```

- [ ] **Step 7: Implement AttachmentRepository**

```csharp
// src/AwesomeImapMcp.ImapClient/Repositories/AttachmentRepository.cs
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.ImapClient.Repositories;

public record AttachmentRecord(
    int Id, int MessageId, string? Filename, string? ContentType,
    int? SizeBytes, string? ContentId, bool IsInline,
    string? LocalPath, string? DownloadedAt);

public class AttachmentRepository(AppDatabase db)
{
    public void Insert(int messageId, string? filename, string? contentType,
        int? sizeBytes, string? contentId, bool isInline)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attachments (message_id, filename, content_type, size_bytes, content_id, is_inline)
            VALUES ($messageId, $filename, $contentType, $sizeBytes, $contentId, $isInline);
            """;
        cmd.Parameters.AddWithValue("$messageId", messageId);
        cmd.Parameters.AddWithValue("$filename", (object?)filename ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentType", (object?)contentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sizeBytes", (object?)sizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentId", (object?)contentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$isInline", isInline ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<AttachmentRecord> GetByMessageId(int messageId)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM attachments WHERE message_id = $messageId;";
        cmd.Parameters.AddWithValue("$messageId", messageId);
        using var reader = cmd.ExecuteReader();
        var list = new List<AttachmentRecord>();
        while (reader.Read())
            list.Add(new AttachmentRecord(
                Id: reader.GetInt32(reader.GetOrdinal("id")),
                MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                Filename: reader.IsDBNull(reader.GetOrdinal("filename")) ? null : reader.GetString(reader.GetOrdinal("filename")),
                ContentType: reader.IsDBNull(reader.GetOrdinal("content_type")) ? null : reader.GetString(reader.GetOrdinal("content_type")),
                SizeBytes: reader.IsDBNull(reader.GetOrdinal("size_bytes")) ? null : reader.GetInt32(reader.GetOrdinal("size_bytes")),
                ContentId: reader.IsDBNull(reader.GetOrdinal("content_id")) ? null : reader.GetString(reader.GetOrdinal("content_id")),
                IsInline: reader.GetInt32(reader.GetOrdinal("is_inline")) == 1,
                LocalPath: reader.IsDBNull(reader.GetOrdinal("local_path")) ? null : reader.GetString(reader.GetOrdinal("local_path")),
                DownloadedAt: reader.IsDBNull(reader.GetOrdinal("downloaded_at")) ? null : reader.GetString(reader.GetOrdinal("downloaded_at"))
            ));
        return list;
    }
}
```

- [ ] **Step 8: Run all tests**

```bash
dotnet test
```

Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(imap-client): add repositories for accounts, folders, messages (with FTS), attachments"
```

---

### Task 9: Thread Builder

**Files:**
- Create: `src/AwesomeImapMcp.ImapClient/ThreadBuilder.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/ThreadBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/ThreadBuilderTests.cs
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class ThreadBuilderTests
{
    [Fact]
    public void ComputeThreadId_NoReferences_UsesMessageId()
    {
        var threadId = ThreadBuilder.ComputeThreadId("<msg1@test.com>", null);
        Assert.NotNull(threadId);
        Assert.NotEmpty(threadId);
    }

    [Fact]
    public void ComputeThreadId_WithReferences_UsesFirstReference()
    {
        var threadId1 = ThreadBuilder.ComputeThreadId("<msg3@test.com>",
            "<root@test.com> <msg2@test.com>");
        var threadId2 = ThreadBuilder.ComputeThreadId("<msg4@test.com>",
            "<root@test.com> <msg3@test.com>");

        // Both should produce same thread_id since root is the same
        Assert.Equal(threadId1, threadId2);
    }

    [Fact]
    public void ComputeThreadId_DifferentRoots_DifferentThreadIds()
    {
        var threadId1 = ThreadBuilder.ComputeThreadId("<msg1@a.com>", null);
        var threadId2 = ThreadBuilder.ComputeThreadId("<msg1@b.com>", null);

        Assert.NotEqual(threadId1, threadId2);
    }

    [Fact]
    public void ComputeThreadId_NullMessageId_ReturnsNull()
    {
        var threadId = ThreadBuilder.ComputeThreadId(null, null);
        Assert.Null(threadId);
    }

    [Fact]
    public void ComputeThreadId_Deterministic()
    {
        var id1 = ThreadBuilder.ComputeThreadId("<msg@test.com>", null);
        var id2 = ThreadBuilder.ComputeThreadId("<msg@test.com>", null);
        Assert.Equal(id1, id2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.ImapClient.Tests
```

Expected: FAIL — `ThreadBuilder` does not exist.

- [ ] **Step 3: Implement ThreadBuilder**

```csharp
// src/AwesomeImapMcp.ImapClient/ThreadBuilder.cs
using System.Security.Cryptography;
using System.Text;

namespace AwesomeImapMcp.ImapClient;

public static class ThreadBuilder
{
    /// <summary>
    /// Computes a thread ID from a message's Message-ID and References header.
    /// The thread ID is SHA256(root_message_id) where root is the first entry
    /// in the References chain, or the message's own Message-ID if no References.
    /// </summary>
    public static string? ComputeThreadId(string? messageId, string? referencesHeader)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        string rootId;

        if (!string.IsNullOrWhiteSpace(referencesHeader))
        {
            // References header is space-separated list of Message-IDs
            // First one is the root of the thread
            var refs = referencesHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rootId = refs[0];
        }
        else
        {
            rootId = messageId;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/AwesomeImapMcp.ImapClient.Tests
```

Expected: All ThreadBuilder tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(imap-client): add thread ID computation from References/In-Reply-To chain"
```

---

### Task 10: Folder Mapper and Message Parser

**Files:**
- Create: `src/AwesomeImapMcp.ImapClient/FolderMapper.cs`
- Create: `src/AwesomeImapMcp.ImapClient/MessageParser.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/FolderMapperTests.cs`
- Create: `tests/AwesomeImapMcp.ImapClient.Tests/MessageParserTests.cs`

- [ ] **Step 1: Write failing tests for FolderMapper**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/FolderMapperTests.cs
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Types;
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class FolderMapperTests
{
    private readonly ProviderProfileRegistry _registry = new();

    [Theory]
    [InlineData(ProviderType.Gmail, FolderRole.Sent, "[Gmail]/Sent Mail")]
    [InlineData(ProviderType.Outlook, FolderRole.Trash, "Deleted Items")]
    [InlineData(ProviderType.Generic, FolderRole.Inbox, "INBOX")]
    public void GetPath_ReturnsProviderSpecificPath(ProviderType provider, FolderRole role, string expected)
    {
        var profile = _registry.GetProfile(provider);
        var mapper = new FolderMapper(profile);
        Assert.Equal(expected, mapper.GetPath(role));
    }

    [Fact]
    public void DetectRole_MatchesKnownPaths()
    {
        var profile = _registry.GetProfile(ProviderType.Gmail);
        var mapper = new FolderMapper(profile);

        Assert.Equal(FolderRole.Sent, mapper.DetectRole("[Gmail]/Sent Mail"));
        Assert.Equal(FolderRole.Inbox, mapper.DetectRole("INBOX"));
        Assert.Null(mapper.DetectRole("Custom Folder"));
    }
}
```

- [ ] **Step 2: Write tests for MessageParser**

```csharp
// tests/AwesomeImapMcp.ImapClient.Tests/MessageParserTests.cs
using AwesomeImapMcp.ImapClient;

namespace AwesomeImapMcp.ImapClient.Tests;

public class MessageParserTests
{
    [Fact]
    public void GenerateSnippet_TruncatesAt200Chars()
    {
        var longText = new string('a', 500);
        var snippet = MessageParser.GenerateSnippet(longText);
        Assert.True(snippet.Length <= 200);
    }

    [Fact]
    public void GenerateSnippet_StripsExtraWhitespace()
    {
        var text = "Hello\n\n\n   World   \t\tfoo";
        var snippet = MessageParser.GenerateSnippet(text);
        Assert.Equal("Hello World foo", snippet);
    }

    [Fact]
    public void GenerateSnippet_NullInput_ReturnsNull()
    {
        Assert.Null(MessageParser.GenerateSnippet(null));
    }

    [Fact]
    public void NormalizeEmail_LowercasesAndTrims()
    {
        Assert.Equal("alice@test.com", MessageParser.NormalizeEmail("  Alice@Test.COM  "));
    }

    [Fact]
    public void ExtractEmailFromAddress_ParsesNameAndEmail()
    {
        Assert.Equal("alice@test.com", MessageParser.ExtractEmailFromAddress("Alice Smith <alice@test.com>"));
    }

    [Fact]
    public void ExtractEmailFromAddress_BareEmail()
    {
        Assert.Equal("alice@test.com", MessageParser.ExtractEmailFromAddress("alice@test.com"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/AwesomeImapMcp.ImapClient.Tests
```

Expected: FAIL.

- [ ] **Step 4: Implement FolderMapper**

```csharp
// src/AwesomeImapMcp.ImapClient/FolderMapper.cs
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.Core.Types;

namespace AwesomeImapMcp.ImapClient;

public class FolderMapper(ProviderProfile profile)
{
    private readonly Dictionary<string, FolderRole> _reverseMap =
        profile.FolderMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public string GetPath(FolderRole role) => profile.FolderMap[role];

    public FolderRole? DetectRole(string path)
    {
        return _reverseMap.TryGetValue(path, out var role) ? role : null;
    }

    public string GetDisplayName(string path, FolderRole? role)
    {
        if (role.HasValue) return role.Value.ToString();
        var parts = path.Split(profile.FolderMap.Values.FirstOrDefault()?.Contains('/') == true ? '/' : '.');
        return parts[^1];
    }
}
```

- [ ] **Step 5: Implement MessageParser**

```csharp
// src/AwesomeImapMcp.ImapClient/MessageParser.cs
using System.Text.RegularExpressions;

namespace AwesomeImapMcp.ImapClient;

public static partial class MessageParser
{
    public static string? GenerateSnippet(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var normalized = WhitespacePattern().Replace(text, " ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    public static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    public static string ExtractEmailFromAddress(string address)
    {
        var match = EmailInAngleBrackets().Match(address);
        if (match.Success)
            return NormalizeEmail(match.Groups[1].Value);
        return NormalizeEmail(address);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex EmailInAngleBrackets();
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test tests/AwesomeImapMcp.ImapClient.Tests
```

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(imap-client): add FolderMapper and MessageParser for IMAP data normalization"
```

---

## Chunk 4: IMAP Connection + Sync

### Task 11: IMAP Connection Manager

**Files:**
- Create: `src/AwesomeImapMcp.ImapClient/ImapConnectionManager.cs`

This wraps MailKit's `ImapClient` with connection pooling and reconnection. No unit tests for this task — it requires a real IMAP server (integration tests in later phases). We verify it compiles and wires up correctly.

- [ ] **Step 1: Implement ImapConnectionManager**

```csharp
// src/AwesomeImapMcp.ImapClient/ImapConnectionManager.cs
using MailKit.Net.Imap;
using MailKit.Security;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Encryption;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace AwesomeImapMcp.ImapClient;

/// <summary>
/// Wraps MailKit ImapClient with connect/auth logic.
/// Phase 1: single connection per manager instance. Connection pooling and
/// reconnection with exponential backoff will be added in Phase 3.
/// </summary>
public sealed class ImapConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private readonly CredentialEncryptor _encryptor;
    private readonly SemaphoreSlim _semaphore;
    private ImapClientLib? _client;
    private bool _disposed;

    public ImapConnectionManager(AccountConfig config, CredentialEncryptor encryptor)
    {
        _config = config;
        _encryptor = encryptor;
        _semaphore = new SemaphoreSlim(1, 1);
    }

    public async Task<ImapClientLib> GetConnectedClientAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true, IsAuthenticated: true })
                return _client;

            _client?.Dispose();
            _client = new ImapClientLib();

            await _client.ConnectAsync(_config.ImapHost, _config.ImapPort,
                SecureSocketOptions.SslOnConnect, ct);

            // Password comes either from config (plaintext/env-substituted) or
            // from encrypted credentials_enc (decrypted via _encryptor at a higher level).
            // Phase 1 uses config.Password directly for simplicity.
            var password = _config.Password ?? "";
            await _client.AuthenticateAsync(_config.Username, password, ct);

            return _client;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client is { IsConnected: true })
            await _client.DisconnectAsync(true, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _semaphore.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(imap-client): add ImapConnectionManager wrapping MailKit with connection management"
```

---

### Task 12: IMAP Sync Service

**Files:**
- Create: `src/AwesomeImapMcp.ImapClient/ImapSyncService.cs`

Initial sync service that connects to IMAP, lists folders, fetches headers, and populates the cache. No unit tests — requires real IMAP server. Verified by compilation and integration test in Task 16.

- [ ] **Step 1: Implement ImapSyncService**

```csharp
// src/AwesomeImapMcp.ImapClient/ImapSyncService.cs
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using AwesomeImapMcp.ImapClient.Repositories;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace AwesomeImapMcp.ImapClient;

public class ImapSyncService(
    FolderRepository folderRepo,
    MessageRepository messageRepo,
    AttachmentRepository attachmentRepo)
{
    /// <summary>
    /// Syncs folder list from IMAP server into the cache.
    /// </summary>
    public async Task SyncFoldersAsync(ImapClientLib client, string accountId,
        FolderMapper mapper, CancellationToken ct = default)
    {
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var folders = await personal.GetSubfoldersAsync(true, ct);

        foreach (var folder in folders)
        {
            var role = mapper.DetectRole(folder.FullName);
            folderRepo.Insert(accountId, folder.FullName,
                mapper.GetDisplayName(folder.FullName, role),
                role?.ToString().ToLowerInvariant(),
                folder.DirectorySeparator.ToString());
        }

        // Always ensure INBOX exists
        folderRepo.Insert(accountId, "INBOX", "Inbox", "inbox", "/");
    }

    /// <summary>
    /// Incrementally syncs message headers for a folder.
    /// Fetches only messages with UID > last_synced_uid.
    /// </summary>
    public async Task SyncFolderMessagesAsync(ImapClientLib client, string accountId,
        FolderRecord folder, CancellationToken ct = default)
    {
        var imapFolder = await client.GetFolderAsync(folder.Path, ct);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        var lastUid = folder.LastSyncedUid;
        var uids = await imapFolder.SearchAsync(
            MailKit.Search.SearchQuery.Uids(
                new UniqueIdRange(new UniqueId((uint)lastUid + 1), UniqueId.MaxValue)),
            ct);

        if (uids.Count == 0)
        {
            await imapFolder.CloseAsync(false, ct);
            return;
        }

        var summaries = await imapFolder.FetchAsync(uids,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags | MessageSummaryItems.Size |
            MessageSummaryItems.BodyStructure |
            MessageSummaryItems.Headers,
            ct);

        var maxUid = lastUid;

        foreach (var summary in summaries)
        {
            var envelope = summary.Envelope;
            var uid = (int)summary.UniqueId.Id;

            if (uid > maxUid) maxUid = uid;

            var messageId = envelope.MessageId;
            var inReplyTo = envelope.InReplyTo;
            var referencesHdr = summary.Headers?["References"];
            var threadId = ThreadBuilder.ComputeThreadId(
                messageId != null ? $"<{messageId}>" : null,
                referencesHdr);

            var from = envelope.From?.FirstOrDefault();
            var fromAddress = from?.ToString();
            var fromEmail = from is MimeKit.MailboxAddress mba
                ? MessageParser.NormalizeEmail(mba.Address)
                : null;

            var toJson = JsonSerializer.Serialize(
                envelope.To?.Select(a => a.ToString()).ToList() ?? []);
            var ccJson = JsonSerializer.Serialize(
                envelope.Cc?.Select(a => a.ToString()).ToList() ?? []);

            var date = envelope.Date?.UtcDateTime.ToString("O")
                ?? DateTimeOffset.UtcNow.ToString("O");
            var dateEpoch = envelope.Date?.ToUnixTimeSeconds();

            var flagsList = summary.Flags.HasValue
                ? GetFlagStrings(summary.Flags.Value, summary.Keywords)
                : [];
            var flagsJson = JsonSerializer.Serialize(flagsList);

            var hasAttachments = summary.Body is MimeKit.BodyPartMultipart multi
                && multi.BodyParts.Any(p => p is MimeKit.BodyPartBasic bpb &&
                    bpb.ContentDisposition?.Disposition == "attachment");

            // Try to get a snippet from a partial body fetch
            string? snippet = null;
            try
            {
                var textPart = imapFolder.GetBodyPart(summary.UniqueId,
                    summary.TextBody ?? summary.HtmlBody!, ct);
                if (textPart is MimeKit.TextPart tp)
                    snippet = MessageParser.GenerateSnippet(tp.Text);
            }
            catch
            {
                snippet = envelope.Subject;
            }

            messageRepo.Insert(accountId, folder.Id, uid,
                messageId != null ? $"<{messageId}>" : null,
                inReplyTo != null ? $"<{inReplyTo}>" : null,
                referencesHdr, threadId,
                envelope.Subject, fromAddress, fromEmail,
                toJson, ccJson, null, date, dateEpoch,
                flagsJson, (int?)summary.Size,
                hasAttachments, snippet);

            // Insert attachment metadata
            if (hasAttachments && summary.Body is MimeKit.BodyPartMultipart multipart)
            {
                foreach (var part in multipart.BodyParts.OfType<MimeKit.BodyPartBasic>())
                {
                    if (part.ContentDisposition?.Disposition == "attachment")
                    {
                        var msgRecord = messageRepo.GetByUid(accountId, folder.Id, uid);
                        if (msgRecord != null)
                        {
                            attachmentRepo.Insert(msgRecord.Id,
                                part.ContentDisposition.FileName,
                                part.ContentType.MimeType,
                                (int?)part.Octets,
                                part.ContentId,
                                false);
                        }
                    }
                }
            }
        }

        folderRepo.UpdateSyncState(folder.Id, maxUid,
            imapFolder.Count, imapFolder.Unread);
        await imapFolder.CloseAsync(false, ct);
    }

    private static List<string> GetFlagStrings(MessageFlags flags, IReadOnlySetOfStrings? keywords)
    {
        var list = new List<string>();
        if (flags.HasFlag(MessageFlags.Seen)) list.Add("\\Seen");
        if (flags.HasFlag(MessageFlags.Answered)) list.Add("\\Answered");
        if (flags.HasFlag(MessageFlags.Flagged)) list.Add("\\Flagged");
        if (flags.HasFlag(MessageFlags.Deleted)) list.Add("\\Deleted");
        if (flags.HasFlag(MessageFlags.Draft)) list.Add("\\Draft");
        if (keywords != null)
            list.AddRange(keywords);
        return list;
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(imap-client): add ImapSyncService for incremental folder and message sync"
```

---

## Chunk 5: MCP Server + Tools + Config Example

### Task 13: MCP Server Entry Point

**Files:**
- Create: `src/AwesomeImapMcp.McpServer/Program.cs`

- [ ] **Step 1: Implement Program.cs**

```csharp
// src/AwesomeImapMcp.McpServer/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Core.Encryption;
using AwesomeImapMcp.Core.Providers;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (required for stdio transport)
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Load config
var configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?.Split('=', 2)[1]
    ?? args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault()
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".awesome-imap-mcp", "config.json");

AppConfig config;
if (File.Exists(configPath))
{
    config = ConfigLoader.LoadFromFile(configPath);
}
else
{
    config = new AppConfig();
}

// Core services
var dbPath = ConfigLoader.ResolveDbPath(config.Cache.DbPath);
var database = new AppDatabase(dbPath);
MigrationRunner.Migrate(database);

var passphrase = Environment.GetEnvironmentVariable("AIMAP_PASSPHRASE");
var encryptor = passphrase != null
    ? new CredentialEncryptor(passphrase)
    : CredentialEncryptor.FromMachineId();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(encryptor);
builder.Services.AddSingleton<ProviderProfileRegistry>();

// Repositories
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddSingleton<FolderRepository>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<AttachmentRepository>();

// MCP Server
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "awesome-imap-mcp", Version = "0.1.0" };
})
.WithStdioServerTransport()
.WithToolsFromAssembly();

await builder.Build().RunAsync();
```

- [ ] **Step 2: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(mcp-server): add entry point with DI wiring, config loading, and MCP stdio transport"
```

---

### Task 14: Account and Folder MCP Tools

**Files:**
- Create: `src/AwesomeImapMcp.McpServer/Tools/AccountTools.cs`
- Create: `src/AwesomeImapMcp.McpServer/Tools/FolderTools.cs`

- [ ] **Step 1: Implement AccountTools**

```csharp
// src/AwesomeImapMcp.McpServer/Tools/AccountTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class AccountTools(AccountRepository accountRepo)
{
    [McpServerTool, Description("List all configured email accounts and their status.")]
    public string ListAccounts()
    {
        var accounts = accountRepo.GetAll();
        var result = accounts.Select(a => new
        {
            a.Id,
            a.Name,
            imap_host = a.ImapHost,
            a.Username,
            a.Provider
        });
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get detailed status for a specific email account.")]
    public string GetAccountStatus(
        [Description("Account ID (e.g., 'personal')")] string accountId)
    {
        var account = accountRepo.GetById(accountId);
        if (account == null)
            return JsonSerializer.Serialize(new { error = $"Account '{accountId}' not found" });

        return JsonSerializer.Serialize(new
        {
            account.Id,
            account.Name,
            imap_host = account.ImapHost,
            imap_port = account.ImapPort,
            account.Username,
            account.Provider,
            auth_type = account.AuthType,
            created_at = account.CreatedAt
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 2: Implement FolderTools**

```csharp
// src/AwesomeImapMcp.McpServer/Tools/FolderTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class FolderTools(FolderRepository folderRepo, MessageRepository messageRepo)
{
    [McpServerTool, Description("List folders for an email account with message and unread counts.")]
    public string ListFolders(
        [Description("Account ID (e.g., 'personal')")] string accountId)
    {
        var folders = folderRepo.GetByAccount(accountId);
        var result = folders.Select(f => new
        {
            f.Id,
            f.Path,
            display_name = f.DisplayName,
            f.Role,
            message_count = f.MessageCount,
            unread_count = f.UnreadCount,
            last_synced_at = f.LastSyncedAt
        });
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get detailed stats for a specific folder.")]
    public string GetFolderStats(
        [Description("Account ID")] string accountId,
        [Description("Folder path (e.g., 'INBOX')")] string folderPath)
    {
        var folder = folderRepo.GetByPath(accountId, folderPath);
        if (folder == null)
            return JsonSerializer.Serialize(new { error = $"Folder '{folderPath}' not found" });

        return JsonSerializer.Serialize(new
        {
            folder.Path,
            display_name = folder.DisplayName,
            folder.Role,
            message_count = folder.MessageCount,
            unread_count = folder.UnreadCount,
            last_synced_uid = folder.LastSyncedUid,
            last_synced_at = folder.LastSyncedAt,
            sync_enabled = folder.SyncEnabled,
            idle_enabled = folder.IdleEnabled,
            poll_interval = folder.PollInterval
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(mcp-server): add account and folder MCP tools"
```

---

### Task 15: Search and Message MCP Tools

**Files:**
- Create: `src/AwesomeImapMcp.McpServer/Tools/SearchTools.cs`
- Create: `src/AwesomeImapMcp.McpServer/Tools/MessageTools.cs`

- [ ] **Step 1: Implement SearchTools**

```csharp
// src/AwesomeImapMcp.McpServer/Tools/SearchTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class SearchTools(MessageRepository messageRepo)
{
    [McpServerTool, Description(
        "Search emails using full-text search. Searches cached emails first (instant). " +
        "Supports searching by subject, body, and sender. " +
        "Use summary_only=true to get compact results without body content.")]
    public string SearchEmails(
        [Description("Search query text")] string query,
        [Description("Account ID to search (optional, searches all if omitted)")] string? accountId = null,
        [Description("Maximum results to return (default: 20)")] int maxResults = 20,
        [Description("If true, return only subject/from/date/snippet without body (default: true)")] bool summaryOnly = true)
    {
        var results = messageRepo.SearchFts(query, accountId, maxResults: maxResults);

        object resultData;
        if (summaryOnly)
        {
            resultData = results.Select(m => new
            {
                m.AccountId,
                m.FolderId,
                m.Uid,
                m.Subject,
                from = m.FromAddress,
                m.Date,
                m.Snippet,
                has_attachments = m.HasAttachments,
                thread_id = m.ThreadId
            });
        }
        else
        {
            resultData = results.Select(m => new
            {
                m.AccountId,
                m.FolderId,
                m.Uid,
                m.Subject,
                from = m.FromAddress,
                to = m.ToAddresses,
                cc = m.CcAddresses,
                m.Date,
                m.Snippet,
                body = m.BodyFetched ? m.BodyText : null,
                body_fetched = m.BodyFetched,
                has_attachments = m.HasAttachments,
                thread_id = m.ThreadId
            });
        }

        return JsonSerializer.Serialize(new
        {
            count = results.Count,
            results = resultData
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 2: Implement MessageTools**

```csharp
// src/AwesomeImapMcp.McpServer/Tools/MessageTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class MessageTools(MessageRepository messageRepo, AttachmentRepository attachmentRepo)
{
    [McpServerTool, Description(
        "Get a single email message by account, folder, and UID. " +
        "Returns full message content including body if cached. " +
        "Use max_body_length to truncate long bodies.")]
    public string GetMessage(
        [Description("Account ID")] string accountId,
        [Description("Folder ID (integer)")] int folderId,
        [Description("Message UID")] int uid,
        [Description("Max body length in characters (0 = unlimited, default: 0)")] int maxBodyLength = 0)
    {
        var msg = messageRepo.GetByUid(accountId, folderId, uid);
        if (msg == null)
            return JsonSerializer.Serialize(new { error = "Message not found" });

        var attachments = attachmentRepo.GetByMessageId(msg.Id);

        var bodyText = msg.BodyText;
        if (maxBodyLength > 0 && bodyText?.Length > maxBodyLength)
            bodyText = bodyText[..maxBodyLength] + "... [truncated]";

        return JsonSerializer.Serialize(new
        {
            msg.AccountId,
            msg.FolderId,
            msg.Uid,
            message_id = msg.MessageId,
            msg.Subject,
            from = msg.FromAddress,
            to = msg.ToAddresses,
            cc = msg.CcAddresses,
            msg.Date,
            body = bodyText,
            body_html = msg.BodyHtml,
            body_fetched = msg.BodyFetched,
            msg.Flags,
            size_bytes = msg.SizeBytes,
            thread_id = msg.ThreadId,
            attachments = attachments.Select(a => new
            {
                a.Filename,
                content_type = a.ContentType,
                size_bytes = a.SizeBytes,
                is_inline = a.IsInline
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description(
        "Get all messages in a conversation thread, ordered by date. " +
        "Uses the thread_id computed from In-Reply-To/References headers.")]
    public string GetThread(
        [Description("Thread ID (hash)")] string threadId,
        [Description("If true, return only summary fields (default: false)")] bool summaryOnly = false)
    {
        var messages = messageRepo.GetByThreadId(threadId);

        object data;
        if (summaryOnly)
        {
            data = messages.Select(m => new
            {
                m.Uid,
                m.Subject,
                from = m.FromAddress,
                m.Date,
                m.Snippet
            });
        }
        else
        {
            data = messages.Select(m => new
            {
                m.Uid,
                m.Subject,
                from = m.FromAddress,
                to = m.ToAddresses,
                m.Date,
                body = m.BodyText,
                body_fetched = m.BodyFetched,
                m.Snippet
            });
        }

        return JsonSerializer.Serialize(new
        {
            thread_id = threadId,
            message_count = messages.Count,
            messages = data
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(mcp-server): add search_emails, get_message, and get_thread MCP tools"
```

---

### Task 16: Config Example + Final Verification

**Files:**
- Create: `config.example.json`

- [ ] **Step 1: Create config.example.json**

```json
{
  "server": {
    "transport": "stdio",
    "http_port": 3846,
    "dashboard_port": 3847,
    "dashboard_enabled": true,
    "dashboard_auth": "pin"
  },
  "accounts": [
    {
      "name": "personal",
      "imap_host": "imap.gmail.com",
      "imap_port": 993,
      "smtp_host": "smtp.gmail.com",
      "smtp_port": 465,
      "smtp_use_ssl": true,
      "username": "you@gmail.com",
      "auth_type": "app_password",
      "password": "${ACCOUNT_PERSONAL_PASSWORD}",
      "provider": "gmail",
      "confirm_mode": "implicit",
      "undo_window_seconds": 10,
      "sync": {
        "idle_folders": ["INBOX"],
        "poll_interval": 300,
        "folders": [
          { "path": "INBOX", "cache_window_days": 60 },
          { "path": "[Gmail]/Sent Mail", "cache_window_days": 14 },
          { "path": "[Gmail]/Drafts" }
        ]
      }
    }
  ],
  "cache": {
    "db_path": "~/.awesome-imap-mcp/cache.db",
    "max_size_mb": 500,
    "default_window_days": 0,
    "max_body_age_days": 0,
    "imap_fallback_ttl_hours": 1
  },
  "queue": {
    "p0_flush_interval": 2,
    "p1_flush_interval": 30,
    "p2_flush_interval": 300,
    "send_undo_window": 10,
    "max_retries": 3
  },
  "llm": {
    "enabled": false,
    "provider": "anthropic",
    "model": "claude-haiku-4-5-20251001",
    "api_key_env": "ANTHROPIC_API_KEY",
    "daily_token_budget": 1000000,
    "monthly_cost_limit": 5.00,
    "auto_analyze_new": false
  },
  "metrics": {
    "internal_retention_days": 7,
    "otlp_endpoint": null,
    "otlp_protocol": "grpc",
    "export_interval_seconds": 15
  }
}
```

- [ ] **Step 2: Run full test suite**

```bash
dotnet test
```

Expected: All tests pass.

- [ ] **Step 3: Run the MCP server briefly to verify it starts**

```bash
timeout 3 dotnet run --project src/AwesomeImapMcp.McpServer 2>/dev/null || true
```

Expected: Server starts without errors (exits due to timeout, or exits cleanly if no stdin).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add config.example.json and verify full Phase 1 build"
```

- [ ] **Step 5: Push to GitHub**

```bash
git push origin main
```
