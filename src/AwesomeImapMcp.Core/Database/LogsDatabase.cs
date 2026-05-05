using Microsoft.Data.Sqlite;

namespace AwesomeImapMcp.Core.Database;

/// <summary>
/// Minimal SQLite wrapper for the logs.db file used for structured application logs.
/// </summary>
public sealed class LogsDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string DbPath => _dbPath;

    public LogsDatabase(string dbPath)
    {
        _dbPath = dbPath;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var writeBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _writeConnection = new SqliteConnection(writeBuilder.ToString());
        _writeConnection.Open();

        using var walCmd = _writeConnection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        using var busyCmd = _writeConnection.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
        busyCmd.ExecuteNonQuery();

        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                level TEXT NOT NULL,
                category TEXT NOT NULL,
                message TEXT NOT NULL,
                exception TEXT,
                metadata TEXT,
                scope TEXT DEFAULT 'system',
                instance_id TEXT DEFAULT '',
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_logs_level ON logs (level, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_logs_category ON logs (category, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_logs_created ON logs (created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_logs_scope ON logs (scope);
            CREATE INDEX IF NOT EXISTS idx_logs_instance ON logs (instance_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes a write action on the shared write connection, serialized via the write lock.
    /// </summary>
    public void ExecuteWrite(Action<SqliteConnection> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try { action(_writeConnection); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Executes a write action on the shared write connection, serialized via the write lock.
    /// Returns a value from the action.
    /// </summary>
    public T ExecuteWrite<T>(Func<SqliteConnection, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try { return action(_writeConnection); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Returns a new read-only connection each time. Caller is responsible for disposing it.
    /// </summary>
    public SqliteConnection GetReadConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var readBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        var conn = new SqliteConnection(readBuilder.ToString());
        conn.Open();

        using var busyCmd = conn.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
        busyCmd.ExecuteNonQuery();

        return conn;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeConnection.Dispose();
        _writeLock.Dispose();
    }
}
