using Microsoft.Data.Sqlite;
using AwesomeImapMcp.Core.Database;

namespace AwesomeImapMcp.Core.Tests.Database;

public class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDatabase _db;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_migration_{Guid.NewGuid():N}.db");
        _db = new AppDatabase(_dbPath);
        MigrationRunner.Migrate(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public void Migrate_CreatesSchemaVersionTable()
    {
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version';";
        var result = cmd.ExecuteScalar();
        Assert.Equal("schema_version", result);
    }

    [Fact]
    public void Migrate_AppliesMigrations_InOrder()
    {
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version WHERE version = 1;";
        var result = cmd.ExecuteScalar();
        Assert.Equal(1L, result);
    }

    [Fact]
    public void Migrate_Idempotent_RunningTwiceNoError()
    {
        // Should not throw
        MigrationRunner.Migrate(_db);
    }

    [Fact]
    public void Migrate_AccountsTable_NotInSqlite()
    {
        // Accounts are stored in accounts.json, not in SQLite
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='accounts';";
        var result = cmd.ExecuteScalar();
        Assert.Null(result);
    }

    [Fact]
    public void Migrate_CreatesMessagesTable()
    {
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='messages';";
        var result = cmd.ExecuteScalar();
        Assert.Equal("messages", result);
    }

    [Fact]
    public void Migrate_CreatesFtsTable()
    {
        var conn = _db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='messages_fts';";
        var result = cmd.ExecuteScalar();
        Assert.Equal("messages_fts", result);
    }
}
