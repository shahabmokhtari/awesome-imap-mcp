using Microsoft.Data.Sqlite;

namespace AwesomeImapMcp.Core.Database;

/// <summary>
/// Permanent SQLite store for labels on accounts whose IMAP servers
/// don't support custom keywords. Unlike cache.db, this file is never cleared.
/// </summary>
public sealed class LabelsDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public LabelsDatabase(string dbPath)
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
            CREATE TABLE IF NOT EXISTS local_labels (
                account_id TEXT NOT NULL,
                message_id TEXT NOT NULL,
                label TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(account_id, message_id, label)
            );
            CREATE INDEX IF NOT EXISTS idx_local_labels_account
                ON local_labels(account_id);
            CREATE INDEX IF NOT EXISTS idx_local_labels_lookup
                ON local_labels(account_id, message_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public void ExecuteWrite(Action<SqliteConnection> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try { action(_writeConnection); }
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
