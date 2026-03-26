using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record FolderRecord(
    int Id, string AccountId, string Path, string? DisplayName,
    string? Role, string Delimiter, string? Flags,
    int MessageCount, int UnreadCount, long LastSyncedUid,
    string? LastSyncedAt, bool SyncEnabled, bool IdleEnabled, int PollInterval,
    string? SyncCursor = null);

public class FolderRepository(AppDatabase db)
{
    public void Insert(string accountId, string path, string? displayName,
        string? role, string delimiter)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO folders (account_id, path, display_name, role, delimiter)
                VALUES ($accountId, $path, $displayName, $role, $delimiter);
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$displayName", (object?)displayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$delimiter", delimiter);
            cmd.ExecuteNonQuery();
        });
    }

    public FolderRecord? GetByPath(string accountId, string path)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM folders WHERE account_id = $accountId AND path = $path;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<FolderRecord> GetByAccount(string accountId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM folders WHERE account_id = $accountId ORDER BY path;";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = cmd.ExecuteReader();
        var list = new List<FolderRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateSyncState(int folderId, long lastSyncedUid, int messageCount, int unreadCount)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE folders SET last_synced_uid = $uid, message_count = $msgCount,
                    unread_count = $unreadCount, last_synced_at = datetime('now')
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.Parameters.AddWithValue("$uid", lastSyncedUid);
            cmd.Parameters.AddWithValue("$msgCount", messageCount);
            cmd.Parameters.AddWithValue("$unreadCount", unreadCount);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Updates the sync cursor for REST-based backends that use cursor-based pagination.
    /// </summary>
    public void UpdateSyncCursor(int folderId, string? syncCursor)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET sync_cursor = $cursor WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.Parameters.AddWithValue("$cursor", (object?)syncCursor ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Resets sync cursor for all folders of an account so they will re-sync.
    /// Only resets last_synced_uid — message_count/unread_count are IMAP server counts
    /// and will be refreshed from the server on next sync.</summary>
    public void ResetSyncState(string accountId)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET last_synced_uid = 0 WHERE account_id = $a;";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Resets sync cursor for a specific folder so it will re-sync.</summary>
    public void ResetFolderSyncState(string accountId, int folderId)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET last_synced_uid = 0 WHERE id = $id AND account_id = $a;";
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Resets sync cursor for ALL folders across all accounts.</summary>
    public void ResetAllSyncState()
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE folders SET last_synced_uid = 0;";
            cmd.ExecuteNonQuery();
        });
    }

    private static FolderRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        Path: r.GetString(r.GetOrdinal("path")),
        DisplayName: r.IsDBNull(r.GetOrdinal("display_name")) ? null : r.GetString(r.GetOrdinal("display_name")),
        Role: r.IsDBNull(r.GetOrdinal("role")) ? null : r.GetString(r.GetOrdinal("role")),
        Delimiter: r.GetString(r.GetOrdinal("delimiter")),
        Flags: r.IsDBNull(r.GetOrdinal("flags")) ? null : r.GetString(r.GetOrdinal("flags")),
        MessageCount: r.GetInt32(r.GetOrdinal("message_count")),
        UnreadCount: r.GetInt32(r.GetOrdinal("unread_count")),
        LastSyncedUid: r.GetInt64(r.GetOrdinal("last_synced_uid")),
        LastSyncedAt: r.IsDBNull(r.GetOrdinal("last_synced_at")) ? null : r.GetString(r.GetOrdinal("last_synced_at")),
        SyncEnabled: r.GetInt32(r.GetOrdinal("sync_enabled")) == 1,
        IdleEnabled: r.GetInt32(r.GetOrdinal("idle_enabled")) == 1,
        PollInterval: r.GetInt32(r.GetOrdinal("poll_interval")),
        SyncCursor: r.IsDBNull(r.GetOrdinal("sync_cursor")) ? null : r.GetString(r.GetOrdinal("sync_cursor"))
    );
}
