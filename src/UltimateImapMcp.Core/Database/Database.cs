using Microsoft.Data.Sqlite;

namespace UltimateImapMcp.Core.Database;

/// <summary>
/// Manages SQLite connections with WAL mode and write serialization.
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <summary>Full path to the SQLite database file.</summary>
    public string DbPath => _dbPath;

    public AppDatabase(string dbPath)
    {
        _dbPath = dbPath;

        // Create directory if needed
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Open and configure the single shared write connection
        var writeBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        };
        _writeConnection = new SqliteConnection(writeBuilder.ToString());
        _writeConnection.Open();

        // Enable WAL mode
        using var walCmd = _writeConnection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the single shared write connection.
    /// Callers should prefer ExecuteWrite/ExecuteWriteAsync to ensure proper serialization.
    /// </summary>
    public SqliteConnection GetWriteConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _writeConnection;
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
    /// Executes an async write action on the shared write connection, serialized via the write lock.
    /// </summary>
    public async Task ExecuteWriteAsync(Func<SqliteConnection, Task> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await action(_writeConnection).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Executes an async write action on the shared write connection, serialized via the write lock.
    /// Returns a value from the action.
    /// </summary>
    public async Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, Task<T>> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { return await action(_writeConnection).ConfigureAwait(false); }
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
            Mode = SqliteOpenMode.ReadOnly,
            ForeignKeys = true
        };
        var conn = new SqliteConnection(readBuilder.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Acquires the write lock asynchronously. Dispose the returned handle to release.
    /// </summary>
    public async Task<IDisposable> AcquireWriteLockAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        return new WriteLockHandle(_writeLock);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeConnection.Dispose();
        _writeLock.Dispose();
    }

    private sealed class WriteLockHandle : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _released;

        public WriteLockHandle(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _semaphore.Release();
        }
    }
}
