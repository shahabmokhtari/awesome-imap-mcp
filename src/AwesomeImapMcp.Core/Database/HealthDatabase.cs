using Microsoft.Data.Sqlite;

namespace AwesomeImapMcp.Core.Database;

/// <summary>
/// Record representing a row in the instance_heartbeats table.
/// </summary>
public record HeartbeatRecord(
    string InstanceId, int ProcessId, string Cwd, string Transport,
    bool IsDashboardHost, string StartedAt, string LastHeartbeat,
    int AccountsCount, long CpuTimeMs, int MemoryMb, bool ShutdownRequested);

/// <summary>
/// Minimal SQLite wrapper for the health.db file used for multi-instance coordination.
/// </summary>
public sealed class HealthDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writeConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string DbPath => _dbPath;

    public HealthDatabase(string dbPath)
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
            CREATE TABLE IF NOT EXISTS instance_heartbeats (
                instance_id TEXT PRIMARY KEY,
                process_id INTEGER NOT NULL,
                cwd TEXT NOT NULL,
                transport TEXT NOT NULL,
                is_dashboard_host INTEGER NOT NULL DEFAULT 0,
                started_at TEXT NOT NULL,
                last_heartbeat TEXT NOT NULL,
                accounts_count INTEGER NOT NULL DEFAULT 0,
                cpu_time_ms INTEGER NOT NULL DEFAULT 0,
                memory_mb INTEGER NOT NULL DEFAULT 0,
                shutdown_requested INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertHeartbeat(string instanceId, int processId, string cwd,
        string transport, bool isDashboardHost, string startedAt,
        string lastHeartbeat, int accountsCount, long cpuTimeMs, int memoryMb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO instance_heartbeats
                    (instance_id, process_id, cwd, transport, is_dashboard_host,
                     started_at, last_heartbeat, accounts_count, cpu_time_ms, memory_mb)
                VALUES ($id, $pid, $cwd, $transport, $dash, $started, $hb, $accounts, $cpu, $mem)
                ON CONFLICT(instance_id) DO UPDATE SET
                    last_heartbeat = $hb,
                    accounts_count = $accounts,
                    cpu_time_ms = $cpu,
                    memory_mb = $mem;
                """;
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.Parameters.AddWithValue("$pid", processId);
            cmd.Parameters.AddWithValue("$cwd", cwd);
            cmd.Parameters.AddWithValue("$transport", transport);
            cmd.Parameters.AddWithValue("$dash", isDashboardHost ? 1 : 0);
            cmd.Parameters.AddWithValue("$started", startedAt);
            cmd.Parameters.AddWithValue("$hb", lastHeartbeat);
            cmd.Parameters.AddWithValue("$accounts", accountsCount);
            cmd.Parameters.AddWithValue("$cpu", cpuTimeMs);
            cmd.Parameters.AddWithValue("$mem", memoryMb);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public List<HeartbeatRecord> GetAllHeartbeats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var conn = GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM instance_heartbeats ORDER BY started_at;";
        using var reader = cmd.ExecuteReader();
        var results = new List<HeartbeatRecord>();
        while (reader.Read())
        {
            results.Add(new HeartbeatRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt64(8),
                reader.GetInt32(9),
                reader.GetInt32(10) != 0));
        }
        return results;
    }

    public void PruneStale(TimeSpan staleThreshold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            var cutoff = DateTime.UtcNow.Subtract(staleThreshold).ToString("o");
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM instance_heartbeats WHERE last_heartbeat < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }

    public bool SetShutdownRequested(string instanceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "UPDATE instance_heartbeats SET shutdown_requested = 1 WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", instanceId);
            return cmd.ExecuteNonQuery() > 0;
        }
        finally { _writeLock.Release(); }
    }

    public void DeleteHeartbeat(string instanceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM instance_heartbeats WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", instanceId);
            cmd.ExecuteNonQuery();
        }
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
