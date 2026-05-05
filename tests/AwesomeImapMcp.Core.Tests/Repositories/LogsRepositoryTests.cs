using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Core.Repositories;

namespace AwesomeImapMcp.Core.Tests.Repositories;

public class LogsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LogsDatabase _db;
    private readonly LogsRepository _repo;

    public LogsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_logs_{Guid.NewGuid()}.db");
        _db = new LogsDatabase(_dbPath);
        _repo = new LogsRepository(_db);
    }

    [Fact]
    public void Write_And_Query_ReturnsLogEntry()
    {
        _repo.Write("Information", "TestCategory", "Hello world");
        var results = _repo.Query();

        Assert.Single(results);
        Assert.Equal("Information", results[0].Level);
        Assert.Equal("TestCategory", results[0].Category);
        Assert.Equal("Hello world", results[0].Message);
        Assert.Equal("system", results[0].Scope);
        Assert.Equal("", results[0].InstanceId);
    }

    [Fact]
    public void Write_WithExceptionAndMetadata()
    {
        _repo.Write("Error", "System", "Something failed",
            exception: "System.Exception: oops", metadata: "{\"key\":\"value\"}");

        var results = _repo.Query(levels: "Error");
        Assert.Single(results);
        Assert.Equal("System.Exception: oops", results[0].Exception);
        Assert.Equal("{\"key\":\"value\"}", results[0].Metadata);
    }

    [Fact]
    public void Write_WithScopeAndInstanceId()
    {
        _repo.Write("Information", "SyncManager", "Sync started",
            scope: "mail", instanceId: "test-instance-1");

        var results = _repo.Query(scope: "mail");
        Assert.Single(results);
        Assert.Equal("mail", results[0].Scope);
        Assert.Equal("test-instance-1", results[0].InstanceId);
    }

    [Fact]
    public void WriteBatch_InsertsMultipleEntries()
    {
        var entries = new List<(string, string, string, string?, string?, string, string)>
        {
            ("Information", "Cat1", "Message 1", null, null, "system", "inst-1"),
            ("Warning", "Cat2", "Message 2", null, null, "mail", "inst-1"),
            ("Error", "Cat1", "Message 3", "exception text", null, "api", "inst-1")
        };

        _repo.WriteBatch(entries);

        var results = _repo.Query(limit: 10);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Query_FiltersByLevel()
    {
        _repo.Write("Information", "Cat", "Info msg");
        _repo.Write("Error", "Cat", "Error msg");
        _repo.Write("Debug", "Cat", "Debug msg");

        var errors = _repo.Query(levels: "Error");
        Assert.Single(errors);
        Assert.Equal("Error msg", errors[0].Message);
    }

    [Fact]
    public void Query_FiltersByCategory()
    {
        _repo.Write("Information", "Sync", "Sync started");
        _repo.Write("Information", "Queue", "Queue flushed");

        var sync = _repo.Query(category: "Sync");
        Assert.Single(sync);
        Assert.Equal("Sync started", sync[0].Message);
    }

    [Fact]
    public void Query_FiltersByScope()
    {
        _repo.Write("Information", "SyncManager", "Sync started", scope: "mail", instanceId: "i1");
        _repo.Write("Information", "QueueWorker", "Queue flushed", scope: "queue", instanceId: "i1");
        _repo.Write("Information", "DashboardHost", "Dashboard starting", scope: "api", instanceId: "i1");

        var mailLogs = _repo.Query(scope: "mail");
        Assert.Single(mailLogs);
        Assert.Equal("Sync started", mailLogs[0].Message);
    }

    [Fact]
    public void Query_FiltersByInstanceId()
    {
        _repo.Write("Information", "Cat", "From instance 1", scope: "system", instanceId: "inst-1");
        _repo.Write("Information", "Cat", "From instance 2", scope: "system", instanceId: "inst-2");

        var inst1Logs = _repo.Query(instanceId: "inst-1");
        Assert.Single(inst1Logs);
        Assert.Equal("From instance 1", inst1Logs[0].Message);
    }

    [Fact]
    public void Query_SearchInMessage()
    {
        _repo.Write("Information", "Cat", "User login succeeded");
        _repo.Write("Information", "Cat", "File upload completed");

        var results = _repo.Query(search: "login");
        Assert.Single(results);
        Assert.Equal("User login succeeded", results[0].Message);
    }

    [Fact]
    public void Query_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            _repo.Write("Information", "Cat", $"Message {i}");

        var results = _repo.Query(limit: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void GetDistinctInstanceIds_ReturnsUniqueIds()
    {
        _repo.Write("Information", "Cat", "msg1", scope: "system", instanceId: "inst-1");
        _repo.Write("Information", "Cat", "msg2", scope: "system", instanceId: "inst-1");
        _repo.Write("Information", "Cat", "msg3", scope: "system", instanceId: "inst-2");

        var ids = _repo.GetDistinctInstanceIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("inst-1", ids);
        Assert.Contains("inst-2", ids);
    }

    [Fact]
    public void Query_FiltersByMultipleLevels()
    {
        _repo.Write("Information", "Cat", "Info msg");
        _repo.Write("Error", "Cat", "Error msg");
        _repo.Write("Debug", "Cat", "Debug msg");
        _repo.Write("Warning", "Cat", "Warning msg");

        var results = _repo.Query(levels: "Error,Warning");
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Level == "Error");
        Assert.Contains(results, r => r.Level == "Warning");
    }

    [Fact]
    public void Query_OffsetSkipsRows()
    {
        for (int i = 0; i < 5; i++)
            _repo.Write("Information", "Cat", $"Message {i}");

        var all = _repo.Query(limit: 10);
        Assert.Equal(5, all.Count);

        var offset2 = _repo.Query(limit: 10, offset: 2);
        Assert.Equal(3, offset2.Count);
    }

    [Fact]
    public void QueryCount_ReturnsTotalMatchingFilters()
    {
        _repo.Write("Information", "Cat", "Info msg 1");
        _repo.Write("Information", "Cat", "Info msg 2");
        _repo.Write("Error", "Cat", "Error msg");

        Assert.Equal(3, _repo.QueryCount());
        Assert.Equal(2, _repo.QueryCount(levels: "Information"));
        Assert.Equal(1, _repo.QueryCount(levels: "Error"));
        Assert.Equal(3, _repo.QueryCount(levels: "Information,Error"));
    }

    [Fact]
    public void Prune_DeletesOldDebugLogs()
    {
        // Insert an old debug log
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO logs (level, category, message, created_at) VALUES ('Debug', 'Test', 'Old debug', datetime('now', '-10 days'));";
            cmd.ExecuteNonQuery();
        });

        // Insert a recent debug log
        _repo.Write("Debug", "Test", "New debug");

        var deleted = _repo.Prune(debugDays: 7, infoDays: 30, errorDays: 90);
        Assert.Equal(1, deleted);

        var remaining = _repo.Query(limit: 100);
        Assert.Single(remaining);
        Assert.Equal("New debug", remaining[0].Message);
    }

    [Fact]
    public void Prune_KeepsRecentErrors()
    {
        // Insert a recent error
        _repo.Write("Error", "Test", "Recent error");

        // Insert an old error
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO logs (level, category, message, created_at) VALUES ('Error', 'Test', 'Old error', datetime('now', '-100 days'));";
            cmd.ExecuteNonQuery();
        });

        var deleted = _repo.Prune(debugDays: 7, infoDays: 30, errorDays: 90);
        Assert.Equal(1, deleted);

        var remaining = _repo.Query(levels: "Error");
        Assert.Single(remaining);
        Assert.Equal("Recent error", remaining[0].Message);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
