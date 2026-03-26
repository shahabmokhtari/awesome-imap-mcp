using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record SyncLogRecord(
    int Id, string AccountId, int? FolderId, string SyncType, string Status,
    int MessagesSynced, string? ErrorMessage, string StartedAt,
    string? CompletedAt, int? DurationMs);

public class SyncLogRepository(AppDatabase db)
{
    /// <summary>
    /// Logs the start of a sync operation. Returns the sync log id.
    /// </summary>
    public int LogStart(string accountId, int? folderId, string syncType)
    {
        return db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_log (account_id, folder_id, sync_type, status)
                VALUES ($accountId, $folderId, $syncType, 'started');
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$syncType", syncType);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
    }

    /// <summary>
    /// Logs the successful completion of a sync operation.
    /// </summary>
    public void LogComplete(int id, int messagesSynced, long durationMs)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_log
                SET status = 'completed',
                    messages_synced = $messagesSynced,
                    completed_at = datetime('now'),
                    duration_ms = $durationMs
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$messagesSynced", messagesSynced);
            cmd.Parameters.AddWithValue("$durationMs", durationMs);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Logs a sync failure with error details.
    /// </summary>
    public void LogError(int id, string errorMessage, long durationMs)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_log
                SET status = 'failed',
                    error_message = $errorMessage,
                    completed_at = datetime('now'),
                    duration_ms = $durationMs
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$errorMessage", errorMessage);
            cmd.Parameters.AddWithValue("$durationMs", durationMs);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns the most recent sync log entries for an account.
    /// </summary>
    public List<SyncLogRecord> GetRecent(string accountId, int limit = 20)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM sync_log
            WHERE account_id = $accountId
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<SyncLogRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    /// <summary>
    /// Returns the most recent sync log entries, optionally filtered by account.
    /// </summary>
    public List<SyncLogRecord> GetRecent(string? accountId, int limit, bool _allAccounts)
    {
        if (accountId is not null)
            return GetRecent(accountId, limit);

        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM sync_log
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<SyncLogRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    private static SyncLogRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        FolderId: r.IsDBNull(r.GetOrdinal("folder_id")) ? null : r.GetInt32(r.GetOrdinal("folder_id")),
        SyncType: r.GetString(r.GetOrdinal("sync_type")),
        Status: r.GetString(r.GetOrdinal("status")),
        MessagesSynced: r.GetInt32(r.GetOrdinal("messages_synced")),
        ErrorMessage: r.IsDBNull(r.GetOrdinal("error_message")) ? null : r.GetString(r.GetOrdinal("error_message")),
        StartedAt: r.GetString(r.GetOrdinal("started_at")),
        CompletedAt: r.IsDBNull(r.GetOrdinal("completed_at")) ? null : r.GetString(r.GetOrdinal("completed_at")),
        DurationMs: r.IsDBNull(r.GetOrdinal("duration_ms")) ? null : r.GetInt32(r.GetOrdinal("duration_ms"))
    );
}
