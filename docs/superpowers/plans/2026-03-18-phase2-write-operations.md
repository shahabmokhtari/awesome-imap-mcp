# Phase 2: Write Operations + Queue Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add send, reply, forward, delete, move, mark, flag, and label operations — all routed through an SQLite-backed operation queue with priority tiers, retry logic, configurable confirm/undo, and dead-letter handling.

**Architecture:** New `UltimateImapMcp.Queue` class library sits between ImapClient and McpServer. Queue operations are persisted in SQLite (`operation_queue` table), processed by a `QueueWorker` BackgroundService with priority-based flush cycles. SMTP via MailKit for sends. Each operation type has a dedicated executor behind `IOperationExecutor`.

**Tech Stack:** .NET 10, MailKit (SMTP), Microsoft.Data.Sqlite, xUnit

**Spec reference:** `docs/superpowers/specs/2026-03-18-ultimate-imap-mcp-design.md` — Queue Package section
**Schema reference:** `docs/DATA_MODEL.md` — operation_queue table

**Dependencies:** Phase 1 complete (Core, ImapClient, McpServer all built, 57 tests passing)

---

## File Structure

### New Project: UltimateImapMcp.Queue

```
src/
  UltimateImapMcp.Queue/
    UltimateImapMcp.Queue.csproj           # References Core + ImapClient
    Models/
      Operation.cs                          # Operation record, OperationType enum, OperationStatus enum
    QueueRepository.cs                      # CRUD for operation_queue table
    QueueManager.cs                         # Enqueue, Cancel, Confirm, List — public API
    QueueWorker.cs                          # BackgroundService: priority-based flush loop
    Executors/
      IOperationExecutor.cs                 # Interface
      SendExecutor.cs                       # SMTP send via MailKit
      DeleteExecutor.cs                     # IMAP STORE +FLAGS \Deleted + EXPUNGE
      MoveExecutor.cs                       # IMAP COPY + DELETE
      FlagExecutor.cs                       # IMAP STORE +/-FLAGS

tests/
  UltimateImapMcp.Queue.Tests/
    UltimateImapMcp.Queue.Tests.csproj
    QueueRepositoryTests.cs
    QueueManagerTests.cs
    QueueWorkerTests.cs
```

### Modified Files

```
src/
  UltimateImapMcp.Core/
    Database/Migrations/
      002_operation_queue.sql               # operation_queue table + sends_at column
  UltimateImapMcp.ImapClient/
    SmtpConnectionManager.cs                # NEW: MailKit SMTP wrapper
  UltimateImapMcp.McpServer/
    Program.cs                              # Add Queue DI registrations + QueueWorker
    Tools/
      ComposeTools.cs                       # NEW: send_email, reply_to, forward
      OrganizeTools.cs                      # NEW: delete, move, mark_read, mark_unread, flag, label
      QueueTools.cs                         # NEW: confirm_send, cancel_operation, list_pending
```

---

## Chunk 1: Queue Project Scaffold + Models + Repository

### Task 1: Queue Project Scaffold

**Files:**
- Create: `src/UltimateImapMcp.Queue/UltimateImapMcp.Queue.csproj`
- Create: `tests/UltimateImapMcp.Queue.Tests/UltimateImapMcp.Queue.Tests.csproj`
- Modify: `UltimateImapMcp.slnx` (add projects)

- [ ] **Step 1: Create Queue project and test project**

```bash
cd ultimate-imap-mcp
dotnet new classlib -n UltimateImapMcp.Queue -o src/UltimateImapMcp.Queue
dotnet new xunit -n UltimateImapMcp.Queue.Tests -o tests/UltimateImapMcp.Queue.Tests
dotnet sln add src/UltimateImapMcp.Queue tests/UltimateImapMcp.Queue.Tests
```

- [ ] **Step 2: Add project references**

```bash
# Queue depends on Core + ImapClient
dotnet add src/UltimateImapMcp.Queue reference src/UltimateImapMcp.Core
dotnet add src/UltimateImapMcp.Queue reference src/UltimateImapMcp.ImapClient

# McpServer depends on Queue
dotnet add src/UltimateImapMcp.McpServer reference src/UltimateImapMcp.Queue

# Queue tests reference Queue + Core
dotnet add tests/UltimateImapMcp.Queue.Tests reference src/UltimateImapMcp.Queue
dotnet add tests/UltimateImapMcp.Queue.Tests reference src/UltimateImapMcp.Core
dotnet add tests/UltimateImapMcp.Queue.Tests reference src/UltimateImapMcp.ImapClient
```

- [ ] **Step 3: Add Microsoft.Extensions.Hosting to Queue (for BackgroundService)**

```bash
dotnet add src/UltimateImapMcp.Queue package Microsoft.Extensions.Hosting
```

- [ ] **Step 4: Delete Class1.cs, verify build**

```bash
rm src/UltimateImapMcp.Queue/Class1.cs
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add Queue project scaffold with references"
```

---

### Task 2: Queue Models + Migration

**Files:**
- Create: `src/UltimateImapMcp.Queue/Models/Operation.cs`
- Create: `src/UltimateImapMcp.Core/Database/Migrations/002_operation_queue.sql`

- [ ] **Step 1: Create Operation models**

```csharp
// src/UltimateImapMcp.Queue/Models/Operation.cs
namespace UltimateImapMcp.Queue.Models;

public enum OperationType
{
    Send, Reply, Forward,
    Delete, Move,
    MarkRead, MarkUnread,
    Flag, Unflag,
    Label, Unlabel,
    BulkDelete, BulkMove
}

public enum OperationStatus
{
    Pending, Confirmed, Processing, Completed, Failed, Cancelled
}

public enum OperationPriority
{
    P0 = 0,  // near-immediate: send, reply, forward
    P1 = 1,  // batched: delete, move, mark, flag, label
    P2 = 2   // background: bulk operations
}

public record QueuedOperation
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string Operation { get; init; }
    public required int Priority { get; init; }
    public required string Status { get; init; }
    public required string Payload { get; init; }
    public bool RequiresConfirm { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? ConfirmedAt { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? CancelledAt { get; init; }
    public string? SendsAt { get; init; }
}

public record EnqueueRequest
{
    public required string AccountId { get; init; }
    public required OperationType Operation { get; init; }
    public required OperationPriority Priority { get; init; }
    public required string Payload { get; init; }
    public bool RequiresConfirm { get; init; }
    public int MaxRetries { get; init; } = 3;
    public string? SendsAt { get; init; }  // for implicit confirm: ISO 8601 timestamp
}
```

- [ ] **Step 2: Create 002_operation_queue.sql migration**

```sql
-- src/UltimateImapMcp.Core/Database/Migrations/002_operation_queue.sql
CREATE TABLE IF NOT EXISTS operation_queue (
    id              TEXT PRIMARY KEY,
    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    operation       TEXT NOT NULL,
    priority        INTEGER NOT NULL DEFAULT 1,
    status          TEXT NOT NULL DEFAULT 'pending',
    payload         TEXT NOT NULL,
    requires_confirm INTEGER DEFAULT 0,
    error_message   TEXT,
    retry_count     INTEGER DEFAULT 0,
    max_retries     INTEGER DEFAULT 3,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    confirmed_at    TEXT,
    started_at      TEXT,
    completed_at    TEXT,
    cancelled_at    TEXT,
    sends_at        TEXT
);
CREATE INDEX IF NOT EXISTS idx_queue_status ON operation_queue(status, priority, created_at);
CREATE INDEX IF NOT EXISTS idx_queue_account ON operation_queue(account_id, status);
```

- [ ] **Step 3: Verify build and migration applies**

```bash
dotnet build && dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(queue): add operation models and queue migration"
```

---

### Task 3: Queue Repository

**Files:**
- Create: `src/UltimateImapMcp.Queue/QueueRepository.cs`
- Create: `tests/UltimateImapMcp.Queue.Tests/QueueRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/UltimateImapMcp.Queue.Tests/QueueRepositoryTests.cs
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Tests;

public class QueueRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly QueueRepository _repo;

    public QueueRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        // Seed an account for FK
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO accounts (id, name, imap_host, username, auth_type, credentials_enc) VALUES ('test', 'Test', 'imap.test.com', 'u@test.com', 'password', 'enc');";
        cmd.ExecuteNonQuery();
        _repo = new QueueRepository(_db);
    }

    [Fact]
    public void Insert_And_GetById_ReturnsOperation()
    {
        var id = _repo.Insert(new EnqueueRequest
        {
            AccountId = "test",
            Operation = OperationType.Send,
            Priority = OperationPriority.P0,
            Payload = """{"to":"bob@test.com","subject":"Hi","body":"Hello"}""",
            RequiresConfirm = true,
            SendsAt = null
        });

        var op = _repo.GetById(id);
        Assert.NotNull(op);
        Assert.Equal("send", op.Operation);
        Assert.Equal(0, op.Priority);
        Assert.Equal("pending", op.Status);
        Assert.True(op.RequiresConfirm);
    }

    [Fact]
    public void UpdateStatus_ChangesStatus()
    {
        var id = _repo.Insert(new EnqueueRequest
        {
            AccountId = "test",
            Operation = OperationType.Delete,
            Priority = OperationPriority.P1,
            Payload = """{"uids":[1,2,3],"folder_id":1}"""
        });

        _repo.UpdateStatus(id, "confirmed");
        var op = _repo.GetById(id);
        Assert.Equal("confirmed", op!.Status);
    }

    [Fact]
    public void GetPendingByPriority_FiltersCorrectly()
    {
        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Send, Priority = OperationPriority.P0, Payload = "{}" });
        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = "{}" });

        // Mark the P0 as confirmed so it shows up in flush query
        var all = _repo.GetByAccount("test");
        var p0Op = all.First(o => o.Priority == 0);
        _repo.UpdateStatus(p0Op.Id, "confirmed");

        var confirmed = _repo.GetConfirmedByPriority(0, limit: 10);
        Assert.Single(confirmed);
        Assert.Equal("send", confirmed[0].Operation);
    }

    [Fact]
    public void Cancel_SetsCancelledStatus()
    {
        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Send, Priority = OperationPriority.P0, Payload = "{}", RequiresConfirm = true });

        var result = _repo.Cancel(id);
        Assert.True(result);

        var op = _repo.GetById(id);
        Assert.Equal("cancelled", op!.Status);
        Assert.NotNull(op.CancelledAt);
    }

    [Fact]
    public void Cancel_AlreadyCompleted_ReturnsFalse()
    {
        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = "{}" });
        _repo.UpdateStatus(id, "completed");

        var result = _repo.Cancel(id);
        Assert.False(result);
    }

    [Fact]
    public void MarkFailed_SetsErrorAndStatus()
    {
        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Send, Priority = OperationPriority.P0, Payload = "{}" });
        _repo.UpdateStatus(id, "confirmed");

        _repo.MarkFailed(id, "SMTP connection refused", incrementRetry: true);
        var op = _repo.GetById(id);
        Assert.Equal("failed", op!.Status);
        Assert.Equal("SMTP connection refused", op.ErrorMessage);
        Assert.Equal(1, op.RetryCount);
    }

    [Fact]
    public void MarkFailed_UnderMaxRetries_SetsBackToConfirmed()
    {
        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Send, Priority = OperationPriority.P0, Payload = "{}", MaxRetries = 3 });
        _repo.UpdateStatus(id, "confirmed");

        _repo.MarkRetryable(id, "Temporary error");
        var op = _repo.GetById(id);
        Assert.Equal("confirmed", op!.Status);  // stays confirmed for retry
        Assert.Equal(1, op.RetryCount);
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

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/UltimateImapMcp.Queue.Tests
```

- [ ] **Step 3: Implement QueueRepository**

```csharp
// src/UltimateImapMcp.Queue/QueueRepository.cs
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public class QueueRepository(AppDatabase db)
{
    public string Insert(EnqueueRequest request)
    {
        var id = Guid.NewGuid().ToString();
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operation_queue (id, account_id, operation, priority, status, payload,
                requires_confirm, max_retries, sends_at)
            VALUES ($id, $accountId, $op, $priority, 'pending', $payload,
                $requiresConfirm, $maxRetries, $sendsAt);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$accountId", request.AccountId);
        cmd.Parameters.AddWithValue("$op", request.Operation.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$priority", (int)request.Priority);
        cmd.Parameters.AddWithValue("$payload", request.Payload);
        cmd.Parameters.AddWithValue("$requiresConfirm", request.RequiresConfirm ? 1 : 0);
        cmd.Parameters.AddWithValue("$maxRetries", request.MaxRetries);
        cmd.Parameters.AddWithValue("$sendsAt", (object?)request.SendsAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    public QueuedOperation? GetById(string id)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM operation_queue WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<QueuedOperation> GetByAccount(string accountId, string? statusFilter = null)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE account_id = $accountId";
        if (statusFilter != null) where += " AND status = $status";
        cmd.CommandText = $"SELECT * FROM operation_queue {where} ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        if (statusFilter != null) cmd.Parameters.AddWithValue("$status", statusFilter);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public List<QueuedOperation> GetAllPending(string? accountId = null)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE status IN ('pending', 'confirmed')";
        if (accountId != null) where += " AND account_id = $accountId";
        cmd.CommandText = $"SELECT * FROM operation_queue {where} ORDER BY priority, created_at;";
        if (accountId != null) cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public List<QueuedOperation> GetConfirmedByPriority(int priority, int limit = 50)
    {
        using var conn = db.GetReadConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM operation_queue
            WHERE status = 'confirmed' AND priority = $priority
            ORDER BY created_at
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$priority", priority);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<QueuedOperation>();
        while (reader.Read()) list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateStatus(string id, string status)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        var timeCol = status switch
        {
            "confirmed" => ", confirmed_at = datetime('now')",
            "processing" => ", started_at = datetime('now')",
            "completed" => ", completed_at = datetime('now')",
            "cancelled" => ", cancelled_at = datetime('now')",
            _ => ""
        };
        cmd.CommandText = $"UPDATE operation_queue SET status = $status{timeCol} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.ExecuteNonQuery();
    }

    public bool Cancel(string id)
    {
        var op = GetById(id);
        if (op == null || op.Status is "completed" or "cancelled" or "processing")
            return false;
        UpdateStatus(id, "cancelled");
        return true;
    }

    public void MarkFailed(string id, string errorMessage, bool incrementRetry = false)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = incrementRetry
            ? "UPDATE operation_queue SET status = 'failed', error_message = $error, retry_count = retry_count + 1 WHERE id = $id;"
            : "UPDATE operation_queue SET status = 'failed', error_message = $error WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", errorMessage);
        cmd.ExecuteNonQuery();
    }

    public void MarkRetryable(string id, string errorMessage)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE operation_queue SET error_message = $error, retry_count = retry_count + 1
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", errorMessage);
        cmd.ExecuteNonQuery();
    }

    private static QueuedOperation ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        AccountId = r.GetString(r.GetOrdinal("account_id")),
        Operation = r.GetString(r.GetOrdinal("operation")),
        Priority = r.GetInt32(r.GetOrdinal("priority")),
        Status = r.GetString(r.GetOrdinal("status")),
        Payload = r.GetString(r.GetOrdinal("payload")),
        RequiresConfirm = r.GetInt32(r.GetOrdinal("requires_confirm")) == 1,
        ErrorMessage = r.IsDBNull(r.GetOrdinal("error_message")) ? null : r.GetString(r.GetOrdinal("error_message")),
        RetryCount = r.GetInt32(r.GetOrdinal("retry_count")),
        MaxRetries = r.GetInt32(r.GetOrdinal("max_retries")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        ConfirmedAt = r.IsDBNull(r.GetOrdinal("confirmed_at")) ? null : r.GetString(r.GetOrdinal("confirmed_at")),
        StartedAt = r.IsDBNull(r.GetOrdinal("started_at")) ? null : r.GetString(r.GetOrdinal("started_at")),
        CompletedAt = r.IsDBNull(r.GetOrdinal("completed_at")) ? null : r.GetString(r.GetOrdinal("completed_at")),
        CancelledAt = r.IsDBNull(r.GetOrdinal("cancelled_at")) ? null : r.GetString(r.GetOrdinal("cancelled_at")),
        SendsAt = r.IsDBNull(r.GetOrdinal("sends_at")) ? null : r.GetString(r.GetOrdinal("sends_at"))
    };
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test
```

Expected: All QueueRepository tests pass + all existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(queue): add QueueRepository with CRUD, cancel, retry, and priority queries"
```

---

## Chunk 2: QueueManager + QueueWorker

### Task 4: QueueManager

**Files:**
- Create: `src/UltimateImapMcp.Queue/QueueManager.cs`
- Create: `tests/UltimateImapMcp.Queue.Tests/QueueManagerTests.cs`

The QueueManager is the public API for enqueueing operations. It wraps QueueRepository and handles the confirm mode logic (implicit vs explicit).

- [ ] **Step 1: Write failing tests**

```csharp
// tests/UltimateImapMcp.Queue.Tests/QueueManagerTests.cs
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Tests;

public class QueueManagerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly QueueManager _manager;

    public QueueManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO accounts (id, name, imap_host, username, auth_type, credentials_enc) VALUES ('test', 'Test', 'imap.test.com', 'u@test.com', 'password', 'enc');";
        cmd.ExecuteNonQuery();
        var repo = new QueueRepository(_db);
        var config = new AppConfig
        {
            Accounts = [new AccountConfig { Name = "test", ImapHost = "imap.test.com", Username = "u@test.com", AuthType = "password", ConfirmMode = "implicit", UndoWindowSeconds = 10 }]
        };
        _manager = new QueueManager(repo, config);
    }

    [Fact]
    public void EnqueueSend_ImplicitConfirm_SetsSendsAt()
    {
        var result = _manager.EnqueueSend("test", """{"to":"bob@test.com","subject":"Hi","body":"Hello"}""");
        Assert.NotNull(result.PendingId);
        Assert.Equal("implicit", result.ConfirmMode);
        Assert.NotNull(result.SendsAt);
    }

    [Fact]
    public void EnqueueSend_ExplicitConfirm_NoSendsAt()
    {
        var config = new AppConfig
        {
            Accounts = [new AccountConfig { Name = "test", ImapHost = "imap.test.com", Username = "u@test.com", AuthType = "password", ConfirmMode = "explicit" }]
        };
        var manager = new QueueManager(new QueueRepository(_db), config);

        var result = manager.EnqueueSend("test", """{"to":"bob@test.com"}""");
        Assert.Equal("explicit", result.ConfirmMode);
        Assert.Null(result.SendsAt);
        Assert.Equal("awaiting_confirmation", result.Status);
    }

    [Fact]
    public void EnqueueOperation_P1_AutoConfirmed()
    {
        var id = _manager.EnqueueOperation("test", OperationType.Delete, """{"uids":[1,2]}""");
        var op = _manager.GetOperation(id);
        Assert.Equal("confirmed", op!.Status);  // P1 ops auto-confirm
    }

    [Fact]
    public void Confirm_PendingSend_ChangesToConfirmed()
    {
        var config = new AppConfig
        {
            Accounts = [new AccountConfig { Name = "test", ImapHost = "imap.test.com", Username = "u@test.com", AuthType = "password", ConfirmMode = "explicit" }]
        };
        var manager = new QueueManager(new QueueRepository(_db), config);

        var result = manager.EnqueueSend("test", """{"to":"bob@test.com"}""");
        var confirmed = manager.Confirm(result.PendingId);
        Assert.True(confirmed);

        var op = manager.GetOperation(result.PendingId);
        Assert.Equal("confirmed", op!.Status);
    }

    [Fact]
    public void Cancel_PendingOperation_Succeeds()
    {
        var result = _manager.EnqueueSend("test", """{"to":"bob@test.com"}""");
        var cancelled = _manager.Cancel(result.PendingId);
        Assert.True(cancelled);
    }

    [Fact]
    public void ListPending_ReturnsAllPendingAndConfirmed()
    {
        _manager.EnqueueSend("test", """{"to":"a@test.com"}""");
        _manager.EnqueueOperation("test", OperationType.Delete, """{"uids":[1]}""");

        var pending = _manager.ListPending("test");
        Assert.Equal(2, pending.Count);
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

- [ ] **Step 2: Implement QueueManager**

```csharp
// src/UltimateImapMcp.Queue/QueueManager.cs
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public record SendEnqueueResult(string PendingId, string ConfirmMode, string Status, string? SendsAt, int? UndoWindowSeconds);

public class QueueManager(QueueRepository repo, AppConfig config)
{
    public SendEnqueueResult EnqueueSend(string accountId, string payload)
    {
        var account = config.Accounts.FirstOrDefault(a =>
            a.Name.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        var confirmMode = account?.ConfirmMode ?? "implicit";
        var undoSeconds = account?.UndoWindowSeconds ?? 10;

        string? sendsAt = null;
        bool requiresConfirm;
        string status;

        if (confirmMode == "explicit")
        {
            requiresConfirm = true;
            status = "awaiting_confirmation";
        }
        else
        {
            requiresConfirm = false;
            sendsAt = DateTime.UtcNow.AddSeconds(undoSeconds).ToString("O");
            status = "will_send_at";
        }

        var id = repo.Insert(new EnqueueRequest
        {
            AccountId = accountId,
            Operation = OperationType.Send,
            Priority = OperationPriority.P0,
            Payload = payload,
            RequiresConfirm = requiresConfirm,
            SendsAt = sendsAt
        });

        // For implicit confirm, auto-confirm immediately (worker checks sends_at)
        if (!requiresConfirm)
            repo.UpdateStatus(id, "confirmed");

        return new SendEnqueueResult(id, confirmMode, status, sendsAt, undoSeconds);
    }

    public string EnqueueOperation(string accountId, OperationType operation, string payload)
    {
        var priority = operation switch
        {
            OperationType.Send or OperationType.Reply or OperationType.Forward => OperationPriority.P0,
            OperationType.BulkDelete or OperationType.BulkMove => OperationPriority.P2,
            _ => OperationPriority.P1
        };

        var id = repo.Insert(new EnqueueRequest
        {
            AccountId = accountId,
            Operation = operation,
            Priority = priority,
            Payload = payload
        });

        // P1/P2 operations auto-confirm (no undo window)
        if (priority != OperationPriority.P0)
            repo.UpdateStatus(id, "confirmed");

        return id;
    }

    public bool Confirm(string pendingId)
    {
        var op = repo.GetById(pendingId);
        if (op == null || op.Status is not ("pending" or "awaiting_confirmation"))
            return false;
        repo.UpdateStatus(pendingId, "confirmed");
        return true;
    }

    public bool Cancel(string pendingId) => repo.Cancel(pendingId);

    public QueuedOperation? GetOperation(string pendingId) => repo.GetById(pendingId);

    public List<QueuedOperation> ListPending(string? accountId = null) =>
        repo.GetAllPending(accountId);
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(queue): add QueueManager with enqueue, confirm, cancel, and implicit/explicit confirm modes"
```

---

### Task 5: Operation Executors

**Files:**
- Create: `src/UltimateImapMcp.Queue/Executors/IOperationExecutor.cs`
- Create: `src/UltimateImapMcp.Queue/Executors/SendExecutor.cs`
- Create: `src/UltimateImapMcp.Queue/Executors/DeleteExecutor.cs`
- Create: `src/UltimateImapMcp.Queue/Executors/MoveExecutor.cs`
- Create: `src/UltimateImapMcp.Queue/Executors/FlagExecutor.cs`
- Create: `src/UltimateImapMcp.ImapClient/SmtpConnectionManager.cs`

No unit tests for executors (they require real IMAP/SMTP). Verify compilation.

- [ ] **Step 1: Create IOperationExecutor interface**

```csharp
// src/UltimateImapMcp.Queue/Executors/IOperationExecutor.cs
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public interface IOperationExecutor
{
    string OperationType { get; }
    Task ExecuteAsync(QueuedOperation operation, CancellationToken ct);
}
```

- [ ] **Step 2: Create SmtpConnectionManager**

```csharp
// src/UltimateImapMcp.ImapClient/SmtpConnectionManager.cs
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using UltimateImapMcp.Core.Configuration;

namespace UltimateImapMcp.ImapClient;

public sealed class SmtpConnectionManager : IDisposable
{
    private readonly AccountConfig _config;
    private SmtpClient? _client;
    private bool _disposed;

    public SmtpConnectionManager(AccountConfig config) { _config = config; }

    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        var client = await GetConnectedClientAsync(ct);
        await client.SendAsync(message, ct);
    }

    private async Task<SmtpClient> GetConnectedClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;

        _client?.Dispose();
        _client = new SmtpClient();

        var smtpHost = _config.SmtpHost ?? _config.ImapHost.Replace("imap.", "smtp.");
        var smtpPort = _config.SmtpPort;
        var options = _config.SmtpUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        await _client.ConnectAsync(smtpHost, smtpPort, options, ct);
        await _client.AuthenticateAsync(_config.Username, _config.Password ?? "", ct);
        return _client;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
```

- [ ] **Step 3: Create SendExecutor**

Uses `SmtpConnectionManager` to send a `MimeMessage` built from the operation payload JSON. Payload schema: `{ "to": "email", "cc": "email", "subject": "text", "body": "text", "in_reply_to": "msg-id", "references": "msg-ids" }`.

```csharp
// src/UltimateImapMcp.Queue/Executors/SendExecutor.cs
using System.Text.Json;
using MimeKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Executors;

public class SendExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "send";

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var accountConfig = config.Accounts.First(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase));

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(accountConfig.Username));
        message.To.Add(MailboxAddress.Parse(payload.GetProperty("to").GetString()!));

        if (payload.TryGetProperty("cc", out var cc) && cc.ValueKind == JsonValueKind.String)
            message.Cc.Add(MailboxAddress.Parse(cc.GetString()!));

        message.Subject = payload.GetProperty("subject").GetString();

        var body = payload.GetProperty("body").GetString() ?? "";
        message.Body = new TextPart("plain") { Text = body };

        if (payload.TryGetProperty("in_reply_to", out var irt) && irt.ValueKind == JsonValueKind.String)
            message.InReplyTo = irt.GetString();

        if (payload.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.String)
            foreach (var r in refs.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                message.References.Add(r);

        using var smtp = new SmtpConnectionManager(accountConfig);
        await smtp.SendAsync(message, ct);
    }
}
```

- [ ] **Step 4: Create DeleteExecutor**

```csharp
// src/UltimateImapMcp.Queue/Executors/DeleteExecutor.cs
using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class DeleteExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "delete";

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;

        var accountConfig = config.Accounts.First(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase));
        var encryptor = Core.Encryption.CredentialEncryptor.FromMachineId();
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var folder = await client.GetFolderAsync(folderPath, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct);
        await folder.ExpungeAsync(ct);
        await folder.CloseAsync(false, ct);
    }
}
```

- [ ] **Step 5: Create MoveExecutor**

```csharp
// src/UltimateImapMcp.Queue/Executors/MoveExecutor.cs
using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class MoveExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "move";

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var fromPath = payload.GetProperty("from_folder").GetString()!;
        var toPath = payload.GetProperty("to_folder").GetString()!;

        var accountConfig = config.Accounts.First(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase));
        var encryptor = Core.Encryption.CredentialEncryptor.FromMachineId();
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var srcFolder = await client.GetFolderAsync(fromPath, ct);
        var dstFolder = await client.GetFolderAsync(toPath, ct);
        await srcFolder.OpenAsync(FolderAccess.ReadWrite, ct);
        await srcFolder.MoveToAsync(uids, dstFolder, ct);
        await srcFolder.CloseAsync(false, ct);
    }
}
```

- [ ] **Step 6: Create FlagExecutor** (handles mark_read, mark_unread, flag, unflag)

```csharp
// src/UltimateImapMcp.Queue/Executors/FlagExecutor.cs
using System.Text.Json;
using MailKit;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.Queue.Models;

using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.Queue.Executors;

public class FlagExecutor(AppConfig config) : IOperationExecutor
{
    public string OperationType => "flag";  // handles flag, unflag, mark_read, mark_unread

    public async Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(operation.Payload);
        var uids = payload.GetProperty("uids").EnumerateArray()
            .Select(u => new UniqueId((uint)u.GetInt32())).ToList();
        var folderPath = payload.GetProperty("folder").GetString()!;

        var (flags, add) = operation.Operation switch
        {
            "mark_read" => (MessageFlags.Seen, true),
            "mark_unread" => (MessageFlags.Seen, false),
            "flag" => (MessageFlags.Flagged, true),
            "unflag" => (MessageFlags.Flagged, false),
            _ => throw new InvalidOperationException($"Unknown flag operation: {operation.Operation}")
        };

        var accountConfig = config.Accounts.First(a =>
            a.Name.Equals(operation.AccountId, StringComparison.OrdinalIgnoreCase));
        var encryptor = Core.Encryption.CredentialEncryptor.FromMachineId();
        using var connMgr = new ImapConnectionManager(accountConfig, encryptor);
        var client = await connMgr.GetConnectedClientAsync(ct);

        var folder = await client.GetFolderAsync(folderPath, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        if (add)
            await folder.AddFlagsAsync(uids, flags, true, ct);
        else
            await folder.RemoveFlagsAsync(uids, flags, true, ct);
        await folder.CloseAsync(false, ct);
    }
}
```

- [ ] **Step 7: Verify build**

```bash
dotnet build
```

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "feat(queue): add operation executors for send, delete, move, flag + SMTP connection manager"
```

---

### Task 6: QueueWorker BackgroundService

**Files:**
- Create: `src/UltimateImapMcp.Queue/QueueWorker.cs`
- Create: `tests/UltimateImapMcp.Queue.Tests/QueueWorkerTests.cs`

The QueueWorker runs a flush loop: P0 every 2s, P1 every 30s, P2 every 5min. For each flush, it picks up confirmed operations and dispatches them to the appropriate executor.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/UltimateImapMcp.Queue.Tests/QueueWorkerTests.cs
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Executors;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Tests;

public class FakeExecutor : IOperationExecutor
{
    public string OperationType { get; init; } = "delete";
    public int ExecuteCount { get; private set; }
    public bool ShouldThrow { get; set; }

    public Task ExecuteAsync(QueuedOperation operation, CancellationToken ct)
    {
        ExecuteCount++;
        if (ShouldThrow) throw new Exception("Fake error");
        return Task.CompletedTask;
    }
}

public class QueueWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly QueueRepository _repo;
    private readonly QueueConfig _config;

    public QueueWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO accounts (id, name, imap_host, username, auth_type, credentials_enc) VALUES ('test', 'Test', 'imap.test.com', 'u@test.com', 'password', 'enc');";
        cmd.ExecuteNonQuery();
        _repo = new QueueRepository(_db);
        _config = new QueueConfig();
    }

    [Fact]
    public async Task FlushPriority_ExecutesConfirmedOps()
    {
        var executor = new FakeExecutor { OperationType = "delete" };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""" });
        // Auto-confirm P1
        var ops = _repo.GetByAccount("test");
        _repo.UpdateStatus(ops[0].Id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, CancellationToken.None);

        Assert.Equal(1, executor.ExecuteCount);
        var op = _repo.GetById(ops[0].Id);
        Assert.Equal("completed", op!.Status);
    }

    [Fact]
    public async Task FlushPriority_FailedOp_RetriesUnderMax()
    {
        var executor = new FakeExecutor { OperationType = "delete", ShouldThrow = true };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""", MaxRetries = 3 });
        var ops = _repo.GetByAccount("test");
        _repo.UpdateStatus(ops[0].Id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, CancellationToken.None);

        var op = _repo.GetById(ops[0].Id);
        Assert.Equal("confirmed", op!.Status);  // still confirmed for retry
        Assert.Equal(1, op.RetryCount);
    }

    [Fact]
    public async Task FlushPriority_FailedOp_ExceedsMaxRetries_MarksFailed()
    {
        var executor = new FakeExecutor { OperationType = "delete", ShouldThrow = true };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""", MaxRetries = 1 });
        _repo.UpdateStatus(id, "confirmed");
        // Simulate already at max retries
        _repo.MarkRetryable(id, "previous error");  // retry_count = 1, but max is 1

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, CancellationToken.None);

        var op = _repo.GetById(id);
        Assert.Equal("failed", op!.Status);
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

- [ ] **Step 2: Implement QueueWorker**

```csharp
// src/UltimateImapMcp.Queue/QueueWorker.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Queue.Executors;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue;

public class QueueWorker(
    QueueRepository repo,
    IEnumerable<IOperationExecutor> executors,
    QueueConfig config,
    ILogger<QueueWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IOperationExecutor> _executors =
        executors.ToDictionary(e => e.OperationType, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var p0Interval = TimeSpan.FromSeconds(config.P0FlushInterval);
        var p1Interval = TimeSpan.FromSeconds(config.P1FlushInterval);
        var p2Interval = TimeSpan.FromSeconds(config.P2FlushInterval);

        var lastP0 = DateTime.UtcNow;
        var lastP1 = DateTime.UtcNow;
        var lastP2 = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            if (now - lastP0 >= p0Interval)
            {
                await FlushPriorityAsync(repo, _executors, 0, ct);
                lastP0 = now;
            }
            if (now - lastP1 >= p1Interval)
            {
                await FlushPriorityAsync(repo, _executors, 1, ct);
                lastP1 = now;
            }
            if (now - lastP2 >= p2Interval)
            {
                await FlushPriorityAsync(repo, _executors, 2, ct);
                lastP2 = now;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public static async Task FlushPriorityAsync(
        QueueRepository repo,
        Dictionary<string, IOperationExecutor> executors,
        int priority,
        CancellationToken ct)
    {
        var operations = repo.GetConfirmedByPriority(priority);

        foreach (var op in operations)
        {
            // For P0 sends with sends_at: check if undo window has passed
            if (op.SendsAt != null && DateTime.Parse(op.SendsAt) > DateTime.UtcNow)
                continue;

            if (!executors.TryGetValue(op.Operation, out var executor))
            {
                repo.MarkFailed(op.Id, $"No executor found for operation type: {op.Operation}");
                continue;
            }

            repo.UpdateStatus(op.Id, "processing");

            try
            {
                await executor.ExecuteAsync(op, ct);
                repo.UpdateStatus(op.Id, "completed");
            }
            catch (Exception ex)
            {
                if (op.RetryCount + 1 >= op.MaxRetries)
                {
                    repo.MarkFailed(op.Id, ex.Message, incrementRetry: true);
                }
                else
                {
                    repo.MarkRetryable(op.Id, ex.Message);
                }
            }
        }
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(queue): add QueueWorker BackgroundService with priority-based flush and retry logic"
```

---

## Chunk 3: MCP Write Tools + Integration

### Task 7: Write MCP Tools

**Files:**
- Create: `src/UltimateImapMcp.McpServer/Tools/ComposeTools.cs`
- Create: `src/UltimateImapMcp.McpServer/Tools/OrganizeTools.cs`
- Create: `src/UltimateImapMcp.McpServer/Tools/QueueTools.cs`

- [ ] **Step 1: Create ComposeTools** (send_email, reply_to, forward)

```csharp
// src/UltimateImapMcp.McpServer/Tools/ComposeTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class ComposeTools(QueueManager queueManager)
{
    [McpServerTool, Description("Queue a new email for sending. Returns a pending_id. Check confirm_mode in the response to know whether to tell the user about the undo window or ask for explicit confirmation.")]
    public string SendEmail(
        [Description("Account ID")] string accountId,
        [Description("Recipient email address")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body (plain text)")] string body,
        [Description("CC recipient (optional)")] string? cc = null)
    {
        var payload = JsonSerializer.Serialize(new { to, cc, subject, body });
        var result = queueManager.EnqueueSend(accountId, payload);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue a reply to an existing email. Returns a pending_id.")]
    public string ReplyTo(
        [Description("Account ID")] string accountId,
        [Description("Original message UID")] int uid,
        [Description("Folder path of the original message")] string folder,
        [Description("Reply body text")] string body,
        [Description("Reply to all recipients (default: false)")] bool replyAll = false)
    {
        var payload = JsonSerializer.Serialize(new { uid, folder, body, reply_all = replyAll });
        var result = queueManager.EnqueueSend(accountId, payload);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue forwarding an email to another recipient. Returns a pending_id.")]
    public string Forward(
        [Description("Account ID")] string accountId,
        [Description("Original message UID")] int uid,
        [Description("Folder path of the original message")] string folder,
        [Description("Recipient email address")] string to,
        [Description("Additional message (optional)")] string? body = null)
    {
        var payload = JsonSerializer.Serialize(new { uid, folder, to, body });
        var result = queueManager.EnqueueSend(accountId, payload);
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 2: Create OrganizeTools** (delete, move, mark, flag, label)

```csharp
// src/UltimateImapMcp.McpServer/Tools/OrganizeTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class OrganizeTools(QueueManager queueManager)
{
    [McpServerTool, Description("Queue deletion of messages. Queued with undo support.")]
    public string DeleteMessages(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs to delete (comma-separated)")] string uids,
        [Description("Folder path")] string folder)
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
        var id = queueManager.EnqueueOperation(accountId, OperationType.Delete, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = "delete" },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue moving messages to another folder.")]
    public string MoveMessages(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs (comma-separated)")] string uids,
        [Description("Source folder path")] string fromFolder,
        [Description("Destination folder path")] string toFolder)
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, from_folder = fromFolder, to_folder = toFolder });
        var id = queueManager.EnqueueOperation(accountId, OperationType.Move, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = "move" },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue marking messages as read.")]
    public string MarkRead(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs (comma-separated)")] string uids,
        [Description("Folder path")] string folder)
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
        var id = queueManager.EnqueueOperation(accountId, OperationType.MarkRead, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = "mark_read" },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue marking messages as unread.")]
    public string MarkUnread(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs (comma-separated)")] string uids,
        [Description("Folder path")] string folder)
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
        var id = queueManager.EnqueueOperation(accountId, OperationType.MarkUnread, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = "mark_unread" },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue flagging/unflagging messages.")]
    public string FlagMessages(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs (comma-separated)")] string uids,
        [Description("Folder path")] string folder,
        [Description("true to flag, false to unflag")] bool set = true)
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, folder });
        var opType = set ? OperationType.Flag : OperationType.Unflag;
        var id = queueManager.EnqueueOperation(accountId, opType, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = opType.ToString().ToLowerInvariant() },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Queue adding/removing a label on messages.")]
    public string LabelMessages(
        [Description("Account ID")] string accountId,
        [Description("Message UIDs (comma-separated)")] string uids,
        [Description("Label name")] string label,
        [Description("'add' or 'remove'")] string action = "add")
    {
        var uidList = uids.Split(',').Select(u => int.Parse(u.Trim())).ToList();
        var payload = JsonSerializer.Serialize(new { uids = uidList, label, action });
        var opType = action == "remove" ? OperationType.Unlabel : OperationType.Label;
        var id = queueManager.EnqueueOperation(accountId, opType, payload);
        return JsonSerializer.Serialize(new { pending_id = id, status = "queued", operation = opType.ToString().ToLowerInvariant() },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 3: Create QueueTools** (confirm, cancel, list)

```csharp
// src/UltimateImapMcp.McpServer/Tools/QueueTools.cs
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Queue;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class QueueTools(QueueManager queueManager)
{
    [McpServerTool, Description("Confirm a pending send operation. Required for accounts with explicit confirm mode.")]
    public string ConfirmSend(
        [Description("Pending operation ID")] string pendingId)
    {
        var confirmed = queueManager.Confirm(pendingId);
        return JsonSerializer.Serialize(new { pending_id = pendingId, confirmed },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Cancel a pending operation before it executes.")]
    public string CancelOperation(
        [Description("Pending operation ID")] string pendingId)
    {
        var cancelled = queueManager.Cancel(pendingId);
        return JsonSerializer.Serialize(new { pending_id = pendingId, cancelled },
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all pending and confirmed operations in the queue.")]
    public string ListPending(
        [Description("Account ID (optional, lists all if omitted)")] string? accountId = null)
    {
        var pending = queueManager.ListPending(accountId);
        var result = pending.Select(op => new
        {
            op.Id,
            op.AccountId,
            op.Operation,
            op.Priority,
            op.Status,
            op.CreatedAt,
            op.SendsAt,
            requires_confirm = op.RequiresConfirm,
            error_message = op.ErrorMessage
        });
        return JsonSerializer.Serialize(new { count = pending.Count, operations = result },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(mcp-server): add compose, organize, and queue management MCP tools"
```

---

### Task 8: Wire Queue into Program.cs

**Files:**
- Modify: `src/UltimateImapMcp.McpServer/Program.cs`

- [ ] **Step 1: Add Queue DI registrations to Program.cs**

Add after the existing repository registrations:

```csharp
// Queue
builder.Services.AddSingleton<QueueRepository>();
builder.Services.AddSingleton<QueueManager>();
builder.Services.AddSingleton(config.Queue);

// Operation executors
builder.Services.AddSingleton<IOperationExecutor, SendExecutor>();
builder.Services.AddSingleton<IOperationExecutor, DeleteExecutor>();
builder.Services.AddSingleton<IOperationExecutor, MoveExecutor>();
builder.Services.AddSingleton<IOperationExecutor, FlagExecutor>();

// Queue worker background service
builder.Services.AddHostedService<QueueWorker>();
```

Add the necessary `using` statements for Queue and Executors namespaces.

- [ ] **Step 2: Verify build and all tests pass**

```bash
dotnet build && dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(mcp-server): wire Queue services and QueueWorker into DI and host"
```

- [ ] **Step 4: Push to GitHub**

```bash
git push origin main
```
