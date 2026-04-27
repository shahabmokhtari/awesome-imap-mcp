using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record MessageRecord(
    int Id, string AccountId, int FolderId, long Uid,
    string? MessageId, string? InReplyTo, string? ReferencesHdr, string? ThreadId,
    string? Subject, string? FromAddress, string? FromEmail,
    string? ToAddresses, string? CcAddresses, string? BccAddresses,
    string Date, long? DateEpoch, string? Flags, int? SizeBytes,
    bool HasAttachments, string? BodyText, string? BodyHtml,
    bool BodyFetched, string? Snippet, string? RawHeaders, string CachedAt);

/// <summary>Volume stats per folder.</summary>
public record EmailVolumeRecord(string FolderPath, int MessageCount, long TotalSizeBytes, int WithAttachments);

/// <summary>Top sender stats.</summary>
public record TopSenderRecord(string FromEmail, int MessageCount, long TotalSizeBytes);

/// <summary>Overall cache statistics.</summary>
public record CacheStatsRecord(int TotalMessages, int BodiesFetched, long DbSizeBytes, long DbFreeSpaceBytes);

/// <summary>Per-account cache statistics.</summary>
public record AccountCacheStatsRecord(
    string AccountId, string AccountName, int MessageCount, int BodiesFetched,
    string? OldestCachedAt, string? NewestCachedAt);

public record SearchFilter
{
    public string? Query { get; init; }
    public string? AccountId { get; init; }
    public int? FolderId { get; init; }
    public string? FromAddress { get; init; }
    public string? ToAddress { get; init; }
    public string? Subject { get; init; }
    public string? Label { get; init; }
    public long? FromDateEpoch { get; init; }
    public long? ToDateEpoch { get; init; }
    public bool? HasAttachments { get; init; }
    public string OrderBy { get; init; } = "date_desc";
    public int MaxResults { get; init; } = 50;
    public int Offset { get; init; } = 0;
}

public class MessageRepository(AppDatabase db)
{
    public void Insert(string accountId, int folderId, long uid, string? messageId,
        string? inReplyTo, string? referencesHdr, string? threadId,
        string? subject, string? fromAddress, string? fromEmail,
        string? toAddresses, string? ccAddresses, string? bccAddresses,
        string date, long? dateEpoch, string? flags, int? sizeBytes,
        bool hasAttachments, string? snippet,
        string? bodyText = null, string? bodyHtml = null, string? rawHeaders = null,
        bool synced = true)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO messages (account_id, folder_id, uid, message_id,
                    in_reply_to, references_hdr, thread_id, subject, from_address, from_email,
                    to_addresses, cc_addresses, bcc_addresses, date, date_epoch, flags,
                    size_bytes, has_attachments, body_text, body_html, body_fetched, snippet, raw_headers, synced)
                VALUES ($accountId, $folderId, $uid, $messageId,
                    $inReplyTo, $referencesHdr, $threadId, $subject, $fromAddress, $fromEmail,
                    $toAddresses, $ccAddresses, $bccAddresses, $date, $dateEpoch, $flags,
                    $sizeBytes, $hasAttachments, $bodyText, $bodyHtml, $bodyFetched, $snippet, $rawHeaders, $synced);
                """;
            cmd.Parameters.AddWithValue("$accountId", accountId);
            cmd.Parameters.AddWithValue("$folderId", folderId);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$inReplyTo", (object?)inReplyTo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$referencesHdr", (object?)referencesHdr ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$threadId", (object?)threadId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fromAddress", (object?)fromAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fromEmail", (object?)fromEmail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$toAddresses", (object?)toAddresses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ccAddresses", (object?)ccAddresses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bccAddresses", (object?)bccAddresses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$date", date);
            cmd.Parameters.AddWithValue("$dateEpoch", (object?)dateEpoch ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$flags", (object?)flags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sizeBytes", (object?)sizeBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hasAttachments", hasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("$bodyText", (object?)bodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bodyHtml", (object?)bodyHtml ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bodyFetched", (bodyText != null || bodyHtml != null) ? 1 : 0);
            cmd.Parameters.AddWithValue("$snippet", (object?)snippet ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rawHeaders", (object?)rawHeaders ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$synced", synced ? 1 : 0);
            cmd.ExecuteNonQuery();

            // Also link to the folder in the junction table
            using var linkCmd = conn.CreateCommand();
            linkCmd.CommandText = """
                INSERT OR IGNORE INTO message_folders (message_id, folder_id, uid)
                VALUES (
                    (SELECT id FROM messages WHERE account_id = $a2 AND folder_id = $f2 AND uid = $u2),
                    $f2, $u2
                );
                """;
            linkCmd.Parameters.AddWithValue("$a2", accountId);
            linkCmd.Parameters.AddWithValue("$f2", folderId);
            linkCmd.Parameters.AddWithValue("$u2", uid);
            linkCmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Inserts a message if it doesn't already exist (by RFC message_id header),
    /// and links it to the specified folder. If the message already exists for
    /// this account (same RFC message_id), only adds a folder link.
    /// Returns the message's database ID.
    /// </summary>
    public int InsertOrLink(string accountId, int folderId, long uid, string? messageId,
        string? inReplyTo, string? referencesHdr, string? threadId,
        string? subject, string? fromAddress, string? fromEmail,
        string? toAddresses, string? ccAddresses, string? bccAddresses,
        string? date, long? dateEpoch, string? flags, int? sizeBytes,
        bool hasAttachments, string? snippet, bool synced = true)
    {
        // Check if this RFC message_id already exists for this account
        if (!string.IsNullOrEmpty(messageId))
        {
            var existing = FindByRfcMessageId(accountId, messageId);
            if (existing is not null)
            {
                // Message already exists — just link to this folder
                LinkToFolder(existing.Id, folderId, uid);
                return existing.Id;
            }
        }

        // New message — insert it
        Insert(accountId, folderId, uid, messageId, inReplyTo, referencesHdr, threadId,
            subject, fromAddress, fromEmail, toAddresses, ccAddresses, bccAddresses,
            date ?? DateTimeOffset.UtcNow.ToString("o"), dateEpoch, flags, sizeBytes, hasAttachments, snippet,
            synced: synced);

        // Get the inserted record's ID and create the junction table link
        var inserted = GetByUidDirect(accountId, folderId, uid);
        if (inserted is not null)
            LinkToFolder(inserted.Id, folderId, uid);
        return inserted?.Id ?? 0;
    }

    public MessageRecord? GetByUid(string accountId, int folderId, long uid)
    {
        using var conn = db.GetReadConnection();

        // Primary lookup via junction table (handles deduplicated messages)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.* FROM messages m
            JOIN message_folders mf ON mf.message_id = m.id
            WHERE m.account_id = $a AND mf.folder_id = $f AND mf.uid = $u
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$u", uid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return ReadRecord(reader);
        reader.Close();

        // Fallback: look directly in messages table (covers messages whose
        // folder_id/uid in the messages table was never linked into message_folders,
        // e.g. due to deduplication or legacy data)
        using var fallbackCmd = conn.CreateCommand();
        fallbackCmd.CommandText = """
            SELECT * FROM messages
            WHERE account_id = $a AND folder_id = $f AND uid = $u AND deleted_at IS NULL
            LIMIT 1;
            """;
        fallbackCmd.Parameters.AddWithValue("$a", accountId);
        fallbackCmd.Parameters.AddWithValue("$f", folderId);
        fallbackCmd.Parameters.AddWithValue("$u", uid);
        using var fallbackReader = fallbackCmd.ExecuteReader();
        return fallbackReader.Read() ? ReadRecord(fallbackReader) : null;
    }

    private MessageRecord? GetByUidDirect(string accountId, int folderId, long uid)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account_id = $a AND folder_id = $f AND uid = $u;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$u", uid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>Look up a message by its database primary key (globally unique).</summary>
    public MessageRecord? GetById(int id)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public List<MessageRecord> SearchFts(string query, string? accountId = null,
        int? folderId = null, int maxResults = 20)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE messages_fts MATCH $query AND m.deleted_at IS NULL";
        if (accountId != null) where += " AND m.account_id = $accountId";
        if (folderId != null) where += " AND m.id IN (SELECT message_id FROM message_folders WHERE folder_id = $folderId)";

        cmd.CommandText = $"""
            SELECT m.* FROM messages m
            JOIN messages_fts ON messages_fts.rowid = m.id
            {where}
            ORDER BY m.date_epoch DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", SanitizeFtsQuery(query));
        if (accountId != null) cmd.Parameters.AddWithValue("$accountId", accountId);
        if (folderId != null) cmd.Parameters.AddWithValue("$folderId", folderId);
        cmd.Parameters.AddWithValue("$limit", maxResults);

        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public List<MessageRecord> SearchAdvanced(SearchFilter filter)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "m.deleted_at IS NULL" };
        var useFts = !string.IsNullOrEmpty(filter.Query);

        if (useFts) conditions.Add("messages_fts MATCH $query");
        if (filter.AccountId is not null) conditions.Add("m.account_id = $accountId");
        if (filter.FolderId is not null) conditions.Add("m.id IN (SELECT message_id FROM message_folders WHERE folder_id = $folderId)");
        if (filter.FromAddress is not null) conditions.Add("m.from_email LIKE $from ESCAPE '\\'");
        if (filter.ToAddress is not null) conditions.Add("m.to_addresses LIKE $to ESCAPE '\\'");
        if (filter.Subject is not null) conditions.Add("m.subject LIKE $subject ESCAPE '\\'");
        if (filter.Label is not null) conditions.Add("m.flags LIKE $label ESCAPE '\\'");
        if (filter.HasAttachments == true) conditions.Add("m.has_attachments = 1");
        if (filter.FromDateEpoch is not null) conditions.Add("m.date_epoch >= $fromDate");
        if (filter.ToDateEpoch is not null) conditions.Add("m.date_epoch <= $toDate");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var orderClause = filter.OrderBy switch
        {
            "date_asc" => "ORDER BY m.date_epoch ASC",
            "from" => "ORDER BY m.from_email ASC",
            "subject" => "ORDER BY m.subject ASC",
            "size_desc" => "ORDER BY m.size_bytes DESC",
            _ => "ORDER BY m.date_epoch DESC"
        };

        var join = useFts ? "JOIN messages_fts ON messages_fts.rowid = m.id" : "";

        cmd.CommandText = $"SELECT m.* FROM messages m {join} {where} {orderClause} LIMIT $limit OFFSET $offset;";

        if (useFts) cmd.Parameters.AddWithValue("$query", SanitizeFtsQuery(filter.Query!));
        if (filter.AccountId is not null) cmd.Parameters.AddWithValue("$accountId", filter.AccountId);
        if (filter.FolderId is not null) cmd.Parameters.AddWithValue("$folderId", filter.FolderId);
        if (filter.FromAddress is not null) cmd.Parameters.AddWithValue("$from", $"%{EscapeLike(filter.FromAddress)}%");
        if (filter.ToAddress is not null) cmd.Parameters.AddWithValue("$to", $"%{EscapeLike(filter.ToAddress)}%");
        if (filter.Subject is not null) cmd.Parameters.AddWithValue("$subject", $"%{EscapeLike(filter.Subject)}%");
        if (filter.Label is not null) cmd.Parameters.AddWithValue("$label", $"%{EscapeLike(filter.Label)}%");
        if (filter.FromDateEpoch is not null) cmd.Parameters.AddWithValue("$fromDate", filter.FromDateEpoch.Value);
        if (filter.ToDateEpoch is not null) cmd.Parameters.AddWithValue("$toDate", filter.ToDateEpoch.Value);
        cmd.Parameters.AddWithValue("$limit", filter.MaxResults);
        cmd.Parameters.AddWithValue("$offset", filter.Offset);

        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public List<MessageRecord> GetByThreadId(string threadId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE thread_id = $threadId ORDER BY date_epoch;";
        cmd.Parameters.AddWithValue("$threadId", threadId);
        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    public void UpdateBody(int messageId, string? bodyText, string? bodyHtml, string? rawHeaders = null)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = rawHeaders is not null
                ? """
                    UPDATE messages SET body_text = $bodyText, body_html = $bodyHtml,
                        raw_headers = $rawHeaders, body_fetched = 1 WHERE id = $id;
                    """
                : """
                    UPDATE messages SET body_text = $bodyText, body_html = $bodyHtml,
                        body_fetched = 1 WHERE id = $id;
                    """;
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$bodyText", (object?)bodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bodyHtml", (object?)bodyHtml ?? DBNull.Value);
            if (rawHeaders is not null)
                cmd.Parameters.AddWithValue("$rawHeaders", rawHeaders);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Gets the maximum (newest) synced UID for a folder. Excludes on-demand fetched messages.</summary>
    public long GetMaxUid(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(mf.uid), 0) FROM message_folders mf
            JOIN messages m ON m.id = mf.message_id
            WHERE m.account_id = $a AND mf.folder_id = $f AND m.synced = 1;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Gets the minimum (oldest) synced UID for a folder. Excludes on-demand fetched messages.</summary>
    public long GetMinUid(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MIN(mf.uid), 0) FROM message_folders mf
            JOIN messages m ON m.id = mf.message_id
            WHERE m.account_id = $a AND mf.folder_id = $f AND m.synced = 1;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Gets all cached UIDs for a folder as a HashSet for fast lookup.</summary>
    public HashSet<long> GetCachedUids(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT mf.uid FROM message_folders mf
            JOIN messages m ON m.id = mf.message_id
            WHERE m.account_id = $a AND mf.folder_id = $f;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (reader.Read())
            set.Add(reader.GetInt64(0));
        return set;
    }

    /// <summary>Soft-delete messages that were removed from the server (marks deleted_at).</summary>
    public int SoftDeleteByUids(string accountId, int folderId, IEnumerable<long> uids)
    {
        var uidList = uids.ToList();
        if (uidList.Count == 0) return 0;

        return db.ExecuteWrite(conn =>
        {
            // Batch UIDs into groups to reduce round-trips
            const int batchSize = 100;
            var count = 0;
            for (var i = 0; i < uidList.Count; i += batchSize)
            {
                var batch = uidList.Skip(i).Take(batchSize).ToList();
                using var cmd = conn.CreateCommand();
                var placeholders = string.Join(", ", batch.Select((_, idx) => $"$u{idx}"));
                cmd.CommandText = $"""
                    UPDATE messages SET deleted_at = datetime('now')
                    WHERE id IN (
                        SELECT mf.message_id FROM message_folders mf
                        JOIN messages m ON m.id = mf.message_id
                        WHERE m.account_id = $a AND mf.folder_id = $f AND mf.uid IN ({placeholders})
                    ) AND deleted_at IS NULL;
                    """;
                cmd.Parameters.AddWithValue("$a", accountId);
                cmd.Parameters.AddWithValue("$f", folderId);
                for (var j = 0; j < batch.Count; j++)
                    cmd.Parameters.AddWithValue($"$u{j}", batch[j]);
                count += cmd.ExecuteNonQuery();
            }
            return count;
        });
    }

    /// <summary>Permanently delete messages that were soft-deleted more than N days ago.</summary>
    public int PurgeSoftDeleted(int retentionDays)
    {
        return db.ExecuteWrite(conn =>
        {
            // Remove junction entries for soft-deleted messages
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = """
                DELETE FROM message_folders WHERE message_id IN (
                    SELECT id FROM messages
                    WHERE deleted_at IS NOT NULL
                      AND deleted_at < datetime('now', $days)
                );
                """;
            unlinkCmd.Parameters.AddWithValue("$days", $"-{retentionDays} days");
            unlinkCmd.ExecuteNonQuery();

            // Remove the messages themselves
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM messages
                WHERE deleted_at IS NOT NULL
                  AND deleted_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{retentionDays} days");
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Gets the most recent messages in a folder, ordered by date descending.
    /// </summary>
    public List<MessageRecord> GetByFolder(string accountId, int folderId, int limit = 50, int offset = 0)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.* FROM messages m
            JOIN message_folders mf ON mf.message_id = m.id
            WHERE mf.folder_id = $f AND m.account_id = $a AND m.deleted_at IS NULL
            ORDER BY m.date_epoch DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        var list = new List<MessageRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    /// <summary>Count total non-deleted messages in a folder (for pagination).</summary>
    public int CountByFolder(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM messages m
            JOIN message_folders mf ON mf.message_id = m.id
            WHERE mf.folder_id = $f AND m.account_id = $a AND m.deleted_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Null out body_text and body_html on the oldest messages (by cached_at)
    /// that have bodies fetched. Returns the number of rows affected.
    /// </summary>
    public int EvictBodies(int batchSize)
    {
        return db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE messages SET body_text = NULL, body_html = NULL, body_fetched = 0
                WHERE id IN (
                    SELECT id FROM messages
                    WHERE body_fetched = 1
                    ORDER BY cached_at ASC
                    LIMIT $batchSize
                );
                """;
            cmd.Parameters.AddWithValue("$batchSize", batchSize);
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Delete the oldest message rows (by cached_at).
    /// Returns the number of rows deleted.
    /// </summary>
    public int EvictMessages(int batchSize)
    {
        return db.ExecuteWrite(conn =>
        {
            // Clean up junction table entries first to prevent orphaned rows
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = """
                DELETE FROM message_folders WHERE message_id IN (
                    SELECT id FROM messages
                    ORDER BY cached_at ASC
                    LIMIT $batchSize
                );
                """;
            unlinkCmd.Parameters.AddWithValue("$batchSize", batchSize);
            unlinkCmd.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM messages WHERE id IN (
                    SELECT id FROM messages
                    ORDER BY cached_at ASC
                    LIMIT $batchSize
                );
                """;
            cmd.Parameters.AddWithValue("$batchSize", batchSize);
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Evict bodies older than the specified number of days.
    /// Returns the number of rows affected.
    /// </summary>
    public int EvictBodiesOlderThan(int days)
    {
        return db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE messages SET body_text = NULL, body_html = NULL, body_fetched = 0
                WHERE body_fetched = 1
                  AND cached_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{days} days");
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Delete messages older than the specified number of days.
    /// Returns the number of rows deleted.
    /// </summary>
    public int EvictMessagesOlderThan(int days)
    {
        return db.ExecuteWrite(conn =>
        {
            // Clean up junction table entries first to prevent orphaned rows
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = """
                DELETE FROM message_folders WHERE message_id IN (
                    SELECT id FROM messages
                    WHERE cached_at < datetime('now', $days)
                );
                """;
            unlinkCmd.Parameters.AddWithValue("$days", $"-{days} days");
            unlinkCmd.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM messages
                WHERE cached_at < datetime('now', $days);
                """;
            cmd.Parameters.AddWithValue("$days", $"-{days} days");
            return cmd.ExecuteNonQuery();
        });
    }

    public int DeleteAll()
    {
        return db.ExecuteWrite(conn =>
        {
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = "DELETE FROM message_folders;";
            unlinkCmd.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages;";
            return cmd.ExecuteNonQuery();
        });
    }

    public int DeleteByAccount(string accountId)
    {
        return db.ExecuteWrite(conn =>
        {
            // Delete message_folders entries for this account's messages
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = """
                DELETE FROM message_folders WHERE message_id IN
                (SELECT id FROM messages WHERE account_id = $a);
                """;
            unlinkCmd.Parameters.AddWithValue("$a", accountId);
            unlinkCmd.ExecuteNonQuery();

            // Delete the messages themselves
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE account_id = $a;";
            cmd.Parameters.AddWithValue("$a", accountId);
            return cmd.ExecuteNonQuery();
        });
    }

    public int DeleteByFolder(string accountId, int folderId)
    {
        return db.ExecuteWrite(conn =>
        {
            // Remove folder links
            using var unlinkCmd = conn.CreateCommand();
            unlinkCmd.CommandText = """
                DELETE FROM message_folders WHERE folder_id = $f
                AND message_id IN (SELECT id FROM messages WHERE account_id = $a);
                """;
            unlinkCmd.Parameters.AddWithValue("$a", accountId);
            unlinkCmd.Parameters.AddWithValue("$f", folderId);
            var unlinked = unlinkCmd.ExecuteNonQuery();

            // Delete orphaned messages (not linked to any folder)
            using var cleanupCmd = conn.CreateCommand();
            cleanupCmd.CommandText = """
                DELETE FROM messages WHERE account_id = $a
                AND id NOT IN (SELECT message_id FROM message_folders);
                """;
            cleanupCmd.Parameters.AddWithValue("$a", accountId);
            cleanupCmd.ExecuteNonQuery();

            return unlinked;
        });
    }

    /// <summary>
    /// Gets email volume stats for an account over the specified number of days.
    /// Returns (total_count, total_size_bytes, folder_path, folder_count) per folder.
    /// Falls back to all-time results when the date-filtered query returns nothing.
    /// </summary>
    public List<EmailVolumeRecord> GetEmailVolume(string accountId, int days = 30)
    {
        var results = GetEmailVolumeInternal(accountId, days);
        if (results.Count > 0) return results;
        // Fall back to all-time if date-filtered returned nothing
        return GetEmailVolumeInternal(accountId, 0);
    }

    private List<EmailVolumeRecord> GetEmailVolumeInternal(string accountId, int days)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        var dateFilter = days > 0 ? "AND m.date_epoch >= $since" : "";
        cmd.CommandText = $"""
            SELECT f.path, COUNT(m.id) as msg_count,
                   COALESCE(SUM(m.size_bytes), 0) as total_size,
                   SUM(CASE WHEN m.has_attachments = 1 THEN 1 ELSE 0 END) as with_attachments
            FROM messages m
            JOIN message_folders mf ON mf.message_id = m.id
            JOIN folders f ON f.id = mf.folder_id
            WHERE m.account_id = $accountId
              AND m.deleted_at IS NULL
              {dateFilter}
            GROUP BY f.path
            ORDER BY msg_count DESC;
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        if (days > 0)
            cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());

        using var reader = cmd.ExecuteReader();
        var list = new List<EmailVolumeRecord>();
        while (reader.Read())
        {
            list.Add(new EmailVolumeRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }
        return list;
    }

    /// <summary>
    /// Gets top senders by message count for an account over the specified days.
    /// Falls back to all-time results when the date-filtered query returns nothing.
    /// </summary>
    public List<TopSenderRecord> GetTopSenders(string accountId, int days = 30, int limit = 10)
    {
        var results = GetTopSendersInternal(accountId, days, limit);
        if (results.Count > 0) return results;
        // Fall back to all-time if date-filtered returned nothing
        return GetTopSendersInternal(accountId, 0, limit);
    }

    private List<TopSenderRecord> GetTopSendersInternal(string accountId, int days, int limit)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        var dateFilter = days > 0 ? "AND date_epoch >= $since" : "";
        cmd.CommandText = $"""
            SELECT from_email, COUNT(*) as msg_count,
                   COALESCE(SUM(size_bytes), 0) as total_size
            FROM messages
            WHERE account_id = $accountId
              AND from_email IS NOT NULL AND from_email != ''
              AND deleted_at IS NULL
              {dateFilter}
            GROUP BY from_email
            ORDER BY msg_count DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        if (days > 0)
            cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var list = new List<TopSenderRecord>();
        while (reader.Read())
        {
            list.Add(new TopSenderRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>Resolves a message from various parameter combinations (shared helper).</summary>
    public MessageRecord? Resolve(int? messageId, string? accountId, int? folderId, int? uid, FolderRepository folderRepo)
    {
        if (messageId is not null)
            return GetById(messageId.Value);

        if (string.IsNullOrEmpty(accountId) || uid is null)
            return null;

        if (folderId is not null)
            return GetByUid(accountId, folderId.Value, uid.Value);

        foreach (var folder in folderRepo.GetByAccount(accountId))
        {
            var msg = GetByUid(accountId, folder.Id, uid.Value);
            if (msg is not null) return msg;
        }

        return null;
    }

    /// <summary>Find a message by RFC Message-ID header within an account.</summary>
    private MessageRecord? FindByRfcMessageId(string accountId, string rfcMessageId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE account_id = $a AND message_id = $mid LIMIT 1;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$mid", rfcMessageId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>Link a message to a folder (add junction table entry).</summary>
    public void LinkToFolder(int messageDbId, int folderId, long uid)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO message_folders (message_id, folder_id, uid) VALUES ($mid, $fid, $uid);";
            cmd.Parameters.AddWithValue("$mid", messageDbId);
            cmd.Parameters.AddWithValue("$fid", folderId);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Repairs orphaned messages that exist in the messages table but have no
    /// corresponding entry in message_folders. Creates junction entries using
    /// the message's folder_id and uid columns. Returns the number of repaired rows.
    /// </summary>
    public int RepairMissingFolderLinks()
    {
        return db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO message_folders (message_id, folder_id, uid)
                SELECT m.id, m.folder_id, m.uid
                FROM messages m
                LEFT JOIN message_folders mf ON mf.message_id = m.id AND mf.folder_id = m.folder_id
                WHERE mf.message_id IS NULL
                  AND m.deleted_at IS NULL
                  AND m.folder_id IS NOT NULL;
                """;
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Update the flags column for a cached message.</summary>
    public void UpdateFlags(int messageId, string? flags)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET flags = $flags WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$flags", (object?)flags ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Remove a folder link from the junction table for a message.</summary>
    public void UnlinkFromFolder(int messageDbId, int folderId)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM message_folders WHERE message_id = $mid AND folder_id = $fid;";
            cmd.Parameters.AddWithValue("$mid", messageDbId);
            cmd.Parameters.AddWithValue("$fid", folderId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Gets overall cache statistics (total messages, bodies fetched, DB size).</summary>
    public CacheStatsRecord GetCacheStats()
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN body_fetched = 1 THEN 1 ELSE 0 END), 0)
            FROM messages WHERE deleted_at IS NULL;
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var totalMessages = reader.GetInt32(0);
        var bodiesFetched = reader.GetInt32(1);
        // File size includes DB + WAL + SHM
        var dbFile = new FileInfo(db.DbPath);
        var dbSize = dbFile.Exists ? dbFile.Length : 0;
        var walFile = new FileInfo(db.DbPath + "-wal");
        var walSize = walFile.Exists ? walFile.Length : 0;
        var totalFileSize = dbSize + walSize;

        // Actual data size = (page_count - freelist_count) * page_size
        using var sizeCmd = conn.CreateCommand();
        sizeCmd.CommandText = "SELECT (page_count - freelist_count) * page_size FROM pragma_page_count, pragma_freelist_count, pragma_page_size;";
        var dataSize = Convert.ToInt64(sizeCmd.ExecuteScalar() ?? 0);

        return new CacheStatsRecord(totalMessages, bodiesFetched, totalFileSize, totalFileSize - dataSize);
    }

    /// <summary>Gets cache statistics broken down by account.</summary>
    public List<AccountCacheStatsRecord> GetCacheStatsByAccount(AccountRepository accountRepo)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id,
                   COUNT(*) as msg_count,
                   COALESCE(SUM(CASE WHEN body_fetched = 1 THEN 1 ELSE 0 END), 0) as bodies,
                   MIN(date) as oldest,
                   MAX(date) as newest
            FROM messages
            WHERE deleted_at IS NULL
            GROUP BY account_id;
            """;
        using var reader = cmd.ExecuteReader();
        var accounts = accountRepo.GetAll().ToDictionary(a => a.Id, a => a.Name);
        var list = new List<AccountCacheStatsRecord>();
        while (reader.Read())
        {
            var accountId = reader.GetString(0);
            list.Add(new AccountCacheStatsRecord(
                AccountId: accountId,
                AccountName: accounts.TryGetValue(accountId, out var name) ? name : accountId,
                MessageCount: reader.GetInt32(1),
                BodiesFetched: reader.GetInt32(2),
                OldestCachedAt: reader.IsDBNull(3) ? null : reader.GetString(3),
                NewestCachedAt: reader.IsDBNull(4) ? null : reader.GetString(4)
            ));
        }
        return list;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Sanitizes user input for SQLite FTS5 MATCH queries.
    /// Wraps the query in double quotes to treat it as a literal phrase,
    /// preventing syntax errors from special characters like . * + - ( ) etc.
    /// </summary>
    private static string SanitizeFtsQuery(string query)
    {
        // Escape any double quotes in the query itself
        var escaped = query.Replace("\"", "\"\"");
        // Wrap in double quotes to treat as a phrase/literal search
        return $"\"{escaped}\"";
    }

    private static MessageRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        FolderId: r.GetInt32(r.GetOrdinal("folder_id")),
        Uid: r.GetInt64(r.GetOrdinal("uid")),
        MessageId: r.IsDBNull(r.GetOrdinal("message_id")) ? null : r.GetString(r.GetOrdinal("message_id")),
        InReplyTo: r.IsDBNull(r.GetOrdinal("in_reply_to")) ? null : r.GetString(r.GetOrdinal("in_reply_to")),
        ReferencesHdr: r.IsDBNull(r.GetOrdinal("references_hdr")) ? null : r.GetString(r.GetOrdinal("references_hdr")),
        ThreadId: r.IsDBNull(r.GetOrdinal("thread_id")) ? null : r.GetString(r.GetOrdinal("thread_id")),
        Subject: r.IsDBNull(r.GetOrdinal("subject")) ? null : r.GetString(r.GetOrdinal("subject")),
        FromAddress: r.IsDBNull(r.GetOrdinal("from_address")) ? null : r.GetString(r.GetOrdinal("from_address")),
        FromEmail: r.IsDBNull(r.GetOrdinal("from_email")) ? null : r.GetString(r.GetOrdinal("from_email")),
        ToAddresses: r.IsDBNull(r.GetOrdinal("to_addresses")) ? null : r.GetString(r.GetOrdinal("to_addresses")),
        CcAddresses: r.IsDBNull(r.GetOrdinal("cc_addresses")) ? null : r.GetString(r.GetOrdinal("cc_addresses")),
        BccAddresses: r.IsDBNull(r.GetOrdinal("bcc_addresses")) ? null : r.GetString(r.GetOrdinal("bcc_addresses")),
        Date: r.GetString(r.GetOrdinal("date")),
        DateEpoch: r.IsDBNull(r.GetOrdinal("date_epoch")) ? null : r.GetInt64(r.GetOrdinal("date_epoch")),
        Flags: r.IsDBNull(r.GetOrdinal("flags")) ? null : r.GetString(r.GetOrdinal("flags")),
        SizeBytes: r.IsDBNull(r.GetOrdinal("size_bytes")) ? null : r.GetInt32(r.GetOrdinal("size_bytes")),
        HasAttachments: r.GetInt32(r.GetOrdinal("has_attachments")) == 1,
        BodyText: r.IsDBNull(r.GetOrdinal("body_text")) ? null : r.GetString(r.GetOrdinal("body_text")),
        BodyHtml: r.IsDBNull(r.GetOrdinal("body_html")) ? null : r.GetString(r.GetOrdinal("body_html")),
        BodyFetched: r.GetInt32(r.GetOrdinal("body_fetched")) == 1,
        Snippet: r.IsDBNull(r.GetOrdinal("snippet")) ? null : r.GetString(r.GetOrdinal("snippet")),
        RawHeaders: r.IsDBNull(r.GetOrdinal("raw_headers")) ? null : r.GetString(r.GetOrdinal("raw_headers")),
        CachedAt: r.GetString(r.GetOrdinal("cached_at"))
    );
}
