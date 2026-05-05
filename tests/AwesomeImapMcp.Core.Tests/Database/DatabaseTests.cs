using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Tests.Database;

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
    public void Constructor_CreatesDatabase_WithoutForeignKeys()
    {
        // Cache DB has ForeignKeys disabled since accounts table moved to config file
        using var db = new AppDatabase(_dbPath);
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = cmd.ExecuteScalar();
        Assert.Equal(0L, result);
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

    [Fact]
    public async Task ExecuteWrite_SerializesConcurrentCalls()
    {
        using var db = new AppDatabase(_dbPath);
        // Create a simple table for concurrency testing
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE counter (value INTEGER NOT NULL);";
            cmd.ExecuteNonQuery();
        });
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO counter (value) VALUES (0);";
            cmd.ExecuteNonQuery();
        });

        // Launch 10 concurrent write tasks that each increment the counter
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            db.ExecuteWrite(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE counter SET value = value + 1;";
                cmd.ExecuteNonQuery();
            });
        })).ToArray();

        await Task.WhenAll(tasks);

        // Verify all 10 increments were applied (no lost updates)
        using var readConn = db.GetReadConnection();
        using var readCmd = readConn.CreateCommand();
        readCmd.CommandText = "SELECT value FROM counter;";
        var result = Convert.ToInt32(readCmd.ExecuteScalar());
        Assert.Equal(10, result);
    }

    [Fact]
    public void Dispose_ThenGetWriteConnection_ThrowsObjectDisposedException()
    {
        var db = new AppDatabase(_dbPath);
        db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => db.GetWriteConnection());
    }

    [Fact]
    public void Dispose_CalledTwice_NoException()
    {
        var db = new AppDatabase(_dbPath);
        db.Dispose();
        var ex = Record.Exception(() => db.Dispose());
        Assert.Null(ex);
    }
}
