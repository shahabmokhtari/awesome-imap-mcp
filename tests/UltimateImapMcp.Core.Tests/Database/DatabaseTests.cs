using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.Core.Tests.Database;

public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // Clean up temp files
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public void Constructor_CreatesDatabase_WithWalMode()
    {
        using var db = new AppDatabase(_dbPath);
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var result = cmd.ExecuteScalar()?.ToString();
        Assert.Equal("wal", result);
    }

    [Fact]
    public void Constructor_CreatesDatabase_WithForeignKeys()
    {
        using var db = new AppDatabase(_dbPath);
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = cmd.ExecuteScalar();
        Assert.Equal(1L, result);
    }

    [Fact]
    public void GetWriteConnection_ReturnsSameConnection()
    {
        using var db = new AppDatabase(_dbPath);
        var conn1 = db.GetWriteConnection();
        var conn2 = db.GetWriteConnection();
        Assert.Same(conn1, conn2);
    }

    [Fact]
    public void GetReadConnection_ReturnsDifferentConnections()
    {
        using var db = new AppDatabase(_dbPath);
        using var conn1 = db.GetReadConnection();
        using var conn2 = db.GetReadConnection();
        Assert.NotSame(conn1, conn2);
    }
}
