using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.Core.Repositories;

namespace AwesomeImapMcp.Core.Tests.Repositories;

public class MetricsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MetricsDatabase _db;
    private readonly MetricsRepository _repo;

    public MetricsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_metrics_{Guid.NewGuid()}.db");
        _db = new MetricsDatabase(_dbPath);
        _repo = new MetricsRepository(_db);
    }

    [Fact]
    public void Record_And_Query_ReturnsMetric()
    {
        _repo.Record("test.counter", 42.0, "key=value");
        var results = _repo.Query("test.counter");

        Assert.Single(results);
        Assert.Equal("test.counter", results[0].Name);
        Assert.Equal(42.0, results[0].Value);
        Assert.Equal("key=value", results[0].Tags);
    }

    [Fact]
    public void Record_WithoutTags_StoresNull()
    {
        _repo.Record("test.gauge", 3.14);
        var results = _repo.Query("test.gauge");

        Assert.Single(results);
        Assert.Null(results[0].Tags);
    }

    [Fact]
    public void Query_FiltersOnName()
    {
        _repo.Record("metric_a", 1.0);
        _repo.Record("metric_b", 2.0);
        _repo.Record("metric_a", 3.0);

        var a = _repo.Query("metric_a");
        Assert.Equal(2, a.Count);

        var b = _repo.Query("metric_b");
        Assert.Single(b);
    }

    [Fact]
    public void Query_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            _repo.Record("test.bulk", i);

        var results = _repo.Query("test.bulk", limit: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void QueryAll_ReturnsAllMetrics()
    {
        _repo.Record("metric_a", 1.0);
        _repo.Record("metric_b", 2.0);

        var all = _repo.QueryAll(limit: 10);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Prune_DeletesOldEntries()
    {
        // Insert an entry manually with an old timestamp
        _db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO metrics (name, value, recorded_at) VALUES ('old', 1.0, datetime('now', '-30 days'));";
            cmd.ExecuteNonQuery();
        });

        _repo.Record("new", 2.0);

        var deleted = _repo.Prune(7);
        Assert.Equal(1, deleted);

        var remaining = _repo.QueryAll(limit: 100);
        Assert.Single(remaining);
        Assert.Equal("new", remaining[0].Name);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
    }
}
