using Microsoft.Extensions.Logging.Abstractions;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Queue;
using UltimateImapMcp.Queue.Executors;
using UltimateImapMcp.Queue.Models;

namespace UltimateImapMcp.Queue.Tests;

public class FakeExecutor : IOperationExecutor
{
    public IReadOnlyList<string> SupportedOperations { get; init; } = ["delete"];
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
        var executor = new FakeExecutor { SupportedOperations = ["delete"] };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""" });
        // Auto-confirm P1
        var ops = _repo.GetByAccount("test");
        _repo.UpdateStatus(ops[0].Id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, executor.ExecuteCount);
        var op = _repo.GetById(ops[0].Id);
        Assert.Equal("completed", op!.Status);
    }

    [Fact]
    public async Task FlushPriority_FailedOp_RetriesUnderMax()
    {
        var executor = new FakeExecutor { SupportedOperations = ["delete"], ShouldThrow = true };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""", MaxRetries = 3 });
        var ops = _repo.GetByAccount("test");
        _repo.UpdateStatus(ops[0].Id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, NullLogger.Instance, CancellationToken.None);

        var op = _repo.GetById(ops[0].Id);
        Assert.Equal("confirmed", op!.Status);  // still confirmed for retry
        Assert.Equal(1, op.RetryCount);
    }

    [Fact]
    public async Task FlushPriority_FailedOp_ExceedsMaxRetries_MarksFailed()
    {
        var executor = new FakeExecutor { SupportedOperations = ["delete"], ShouldThrow = true };
        var executors = new Dictionary<string, IOperationExecutor> { ["delete"] = executor };

        var id = _repo.Insert(new EnqueueRequest { AccountId = "test", Operation = OperationType.Delete, Priority = OperationPriority.P1, Payload = """{"uids":[1],"folder":"INBOX"}""", MaxRetries = 1 });
        _repo.UpdateStatus(id, "confirmed");
        // Simulate already at max retries
        _repo.MarkRetryable(id, "previous error");  // retry_count = 1, but max is 1

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, NullLogger.Instance, CancellationToken.None);

        var op = _repo.GetById(id);
        Assert.Equal("failed", op!.Status);
    }

    [Fact]
    public async Task FlushPriority_SendsAtInFuture_SkipsOperation()
    {
        var executor = new FakeExecutor { SupportedOperations = ["send"] };
        var executors = new Dictionary<string, IOperationExecutor> { ["send"] = executor };

        // Enqueue with sends_at 60 seconds from now (well within undo window)
        var sendsAt = DateTime.UtcNow.AddSeconds(60).ToString("O");
        var id = _repo.Insert(new EnqueueRequest
        {
            AccountId = "test",
            Operation = OperationType.Send,
            Priority = OperationPriority.P0,
            Payload = """{"to":"bob@test.com","subject":"Hi","body":"Hello"}""",
            SendsAt = sendsAt
        });
        _repo.UpdateStatus(id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 0, NullLogger.Instance, CancellationToken.None);

        // Should NOT have been executed because sends_at is in the future
        Assert.Equal(0, executor.ExecuteCount);
        var op = _repo.GetById(id);
        // Still confirmed (not completed), but was claimed for processing then skipped
        // Actually the skip happens before TryClaimForProcessing, so status stays confirmed
        Assert.Equal("confirmed", op!.Status);
    }

    [Fact]
    public async Task FlushPriority_SendsAtInPast_ExecutesOperation()
    {
        var executor = new FakeExecutor { SupportedOperations = ["send"] };
        var executors = new Dictionary<string, IOperationExecutor> { ["send"] = executor };

        // Enqueue with sends_at 10 seconds in the past (undo window expired)
        var sendsAt = DateTime.UtcNow.AddSeconds(-10).ToString("O");
        var id = _repo.Insert(new EnqueueRequest
        {
            AccountId = "test",
            Operation = OperationType.Send,
            Priority = OperationPriority.P0,
            Payload = """{"to":"bob@test.com","subject":"Hi","body":"Hello"}""",
            SendsAt = sendsAt
        });
        _repo.UpdateStatus(id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 0, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, executor.ExecuteCount);
        var op = _repo.GetById(id);
        Assert.Equal("completed", op!.Status);
    }

    [Fact]
    public async Task FlushPriority_NoExecutorFound_MarksFailed()
    {
        // Register no executors for the "delete" operation type
        var executors = new Dictionary<string, IOperationExecutor>();

        var id = _repo.Insert(new EnqueueRequest
        {
            AccountId = "test",
            Operation = OperationType.Delete,
            Priority = OperationPriority.P1,
            Payload = """{"uids":[1],"folder":"INBOX"}"""
        });
        _repo.UpdateStatus(id, "confirmed");

        await QueueWorker.FlushPriorityAsync(_repo, executors, 1, NullLogger.Instance, CancellationToken.None);

        var op = _repo.GetById(id);
        Assert.Equal("failed", op!.Status);
        Assert.Contains("No executor found", op.ErrorMessage);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
