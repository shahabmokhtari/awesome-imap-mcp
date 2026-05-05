using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Tests.Database;

public class HealthDatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public HealthDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"health_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public void Upsert_and_read()
    {
        using var db = new HealthDatabase(_dbPath);
        var now = DateTime.UtcNow.ToString("o");
        db.UpsertHeartbeat("inst-1", 1234, "/tmp", "stdio", true, now, now, 3, 500, 128);

        var all = db.GetAllHeartbeats();
        Assert.Single(all);
        var hb = all[0];
        Assert.Equal("inst-1", hb.InstanceId);
        Assert.Equal(1234, hb.ProcessId);
        Assert.Equal("/tmp", hb.Cwd);
        Assert.Equal("stdio", hb.Transport);
        Assert.True(hb.IsDashboardHost);
        Assert.Equal(now, hb.StartedAt);
        Assert.Equal(now, hb.LastHeartbeat);
        Assert.Equal(3, hb.AccountsCount);
        Assert.Equal(500, hb.CpuTimeMs);
        Assert.Equal(128, hb.MemoryMb);
        Assert.False(hb.ShutdownRequested);
    }

    [Fact]
    public void Upsert_updates_existing()
    {
        using var db = new HealthDatabase(_dbPath);
        var started = DateTime.UtcNow.ToString("o");
        db.UpsertHeartbeat("inst-1", 1234, "/tmp", "stdio", false, started, started, 1, 100, 64);

        var later = DateTime.UtcNow.AddSeconds(10).ToString("o");
        db.UpsertHeartbeat("inst-1", 1234, "/tmp", "stdio", false, started, later, 5, 200, 96);

        var all = db.GetAllHeartbeats();
        Assert.Single(all);
        var hb = all[0];
        Assert.Equal(later, hb.LastHeartbeat);
        Assert.Equal(5, hb.AccountsCount);
        Assert.Equal(200, hb.CpuTimeMs);
        Assert.Equal(96, hb.MemoryMb);
    }

    [Fact]
    public void PruneStale_removes_old()
    {
        using var db = new HealthDatabase(_dbPath);
        var old = DateTime.UtcNow.AddMinutes(-10).ToString("o");
        var fresh = DateTime.UtcNow.ToString("o");

        db.UpsertHeartbeat("stale", 1, "/", "stdio", false, old, old, 0, 0, 0);
        db.UpsertHeartbeat("fresh", 2, "/", "stdio", false, fresh, fresh, 0, 0, 0);

        db.PruneStale(TimeSpan.FromMinutes(5));

        var all = db.GetAllHeartbeats();
        Assert.Single(all);
        Assert.Equal("fresh", all[0].InstanceId);
    }

    [Fact]
    public void SetShutdownRequested_flags()
    {
        using var db = new HealthDatabase(_dbPath);
        var now = DateTime.UtcNow.ToString("o");
        db.UpsertHeartbeat("inst-1", 1, "/", "stdio", false, now, now, 0, 0, 0);

        var result = db.SetShutdownRequested("inst-1");
        Assert.True(result);

        var all = db.GetAllHeartbeats();
        Assert.True(all[0].ShutdownRequested);
    }

    [Fact]
    public void SetShutdownRequested_returns_false_for_missing()
    {
        using var db = new HealthDatabase(_dbPath);
        var result = db.SetShutdownRequested("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public void DeleteHeartbeat_removes_row()
    {
        using var db = new HealthDatabase(_dbPath);
        var now = DateTime.UtcNow.ToString("o");
        db.UpsertHeartbeat("inst-1", 1, "/", "stdio", false, now, now, 0, 0, 0);
        db.UpsertHeartbeat("inst-2", 2, "/", "stdio", false, now, now, 0, 0, 0);

        db.DeleteHeartbeat("inst-1");

        var all = db.GetAllHeartbeats();
        Assert.Single(all);
        Assert.Equal("inst-2", all[0].InstanceId);
    }
}
