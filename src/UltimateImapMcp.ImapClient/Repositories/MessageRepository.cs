using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record MessageRecord(
    int Id, string AccountId, int FolderId, int Uid,
    string? MessageId, string? InReplyTo, string? ReferencesHdr, string? ThreadId,
    string? Subject, string? FromAddress, string? FromEmail,
    string? ToAddresses, string? CcAddresses, string? BccAddresses,
    string Date, long? DateEpoch, string? Flags, int? SizeBytes,
    bool HasAttachments, string? BodyText, string? BodyHtml,
    bool BodyFetched, string? Snippet, string? RawHeaders, string CachedAt);

public class MessageRepository(AppDatabase db)
{
    public void Insert(string accountId, int folderId, int uid, string? messageId,
        string? inReplyTo, string? referencesHdr, string? threadId,
        string? subject, string? fromAddress, string? fromEmail,
        string? toAddresses, string? ccAddresses, string? bccAddresses,
        string date, long? dateEpoch, string? flags, int? sizeBytes,
        bool hasAttachments, string? snippet,
        string? bodyText = null, string? bodyHtml = null, string? rawHeaders = null)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages (account_id, folder_id, uid, message_id,
                in_reply_to, references_hdr, thread_id, subject, from_address, from_email,
                to_addresses, cc_addresses, bcc_addresses, date, date_epoch, flags,
                size_bytes, has_attachments, body_text, body_html, body_fetched, snippet, raw_headers)
            VALUES ($accountId, $folderId, $uid, $messageId,
                $inReplyTo, $referencesHdr, $threadId, $subject, $fromAddress, $fromEmail,
                $toAddresses, $ccAddresses, $bccAddresses, $date, $dateEpoch, $flags,
                $sizeBytes, $hasAttachments, $bodyText, $bodyHtml, $bodyFetched, $snippet, $rawHeaders);
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
        cmd.ExecuteNonQuery();
    }

    public MessageRecord? GetByUid(string accountId, int folderId, int uid)
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

    public List<MessageRecord> SearchFts(string query, string? accountId = null,
        int? folderId = null, int maxResults = 20)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var where = "WHERE messages_fts MATCH $query";
        if (accountId != null) where += " AND m.account_id = $accountId";
        if (folderId != null) where += " AND m.folder_id = $folderId";

        cmd.CommandText = $"""
            SELECT m.* FROM messages m
            JOIN messages_fts ON messages_fts.rowid = m.id
            {where}
            ORDER BY m.date_epoch DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$query", query);
        if (accountId != null) cmd.Parameters.AddWithValue("$accountId", accountId);
        if (folderId != null) cmd.Parameters.AddWithValue("$folderId", folderId);
        cmd.Parameters.AddWithValue("$limit", maxResults);

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

    public void UpdateBody(int messageId, string? bodyText, string? bodyHtml)
    {
        var conn = db.GetWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET body_text = $bodyText, body_html = $bodyHtml,
                body_fetched = 1 WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$bodyText", (object?)bodyText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bodyHtml", (object?)bodyHtml ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public int GetMaxUid(string accountId, int folderId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(uid), 0) FROM messages
            WHERE account_id = $a AND folder_id = $f;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$f", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static MessageRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Id: r.GetInt32(r.GetOrdinal("id")),
        AccountId: r.GetString(r.GetOrdinal("account_id")),
        FolderId: r.GetInt32(r.GetOrdinal("folder_id")),
        Uid: r.GetInt32(r.GetOrdinal("uid")),
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
