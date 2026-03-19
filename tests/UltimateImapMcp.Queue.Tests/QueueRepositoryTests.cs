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
