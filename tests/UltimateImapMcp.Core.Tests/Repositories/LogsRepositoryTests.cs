using UltimateImapMcp.Core.Database;
using UltimateImapMcp.Core.Repositories;

namespace UltimateImapMcp.Core.Tests.Repositories;

public class LogsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;
    private readonly LogsRepository _repo;

    public LogsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_logs_{Guid.NewGuid()}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
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
    }

    [Fact]
    public void Write_WithExceptionAndMetadata()
    {
        _repo.Write("Error", "System", "Something failed",
            exception: "System.Exception: oops", metadata: "{\"key\":\"value\"}");

        var results = _repo.Query(level: "Error");
        Assert.Single(results);
        Assert.Equal("System.Exception: oops", results[0].Exception);
        Assert.Equal("{\"key\":\"value\"}", results[0].Metadata);
    }

    [Fact]
    public void WriteBatch_InsertsMultipleEntries()
    {
        var entries = new List<(string, string, string, string?, string?)>
        {
            ("Information", "Cat1", "Message 1", null, null),
            ("Warning", "Cat2", "Message 2", null, null),
            ("Error", "Cat1", "Message 3", "exception text", null)
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

        var errors = _repo.Query(level: "Error");
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
    public void Prune_DeletesOldDebugLogs()
    {
        // Insert an old debug log
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO logs (level, category, message, created_at) VALUES ('Debug', 'Test', 'Old debug', datetime('now', '-10 days'));";
        cmd.ExecuteNonQuery();

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
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO logs (level, category, message, created_at) VALUES ('Error', 'Test', 'Old error', datetime('now', '-100 days'));";
        cmd.ExecuteNonQuery();

        var deleted = _repo.Prune(debugDays: 7, infoDays: 30, errorDays: 90);
        Assert.Equal(1, deleted);

        var remaining = _repo.Query(level: "Error");
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
