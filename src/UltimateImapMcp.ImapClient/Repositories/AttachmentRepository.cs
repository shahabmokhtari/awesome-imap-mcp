using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record AttachmentRecord(
    int Id, int MessageId, string? Filename, string? ContentType,
    int? SizeBytes, string? ContentId, bool IsInline,
    string? LocalPath, string? DownloadedAt);

/// <summary>Attachment search results include parent message context.</summary>
public record AttachmentSearchResult(
    int AttachmentId, string? Filename, string? ContentType, int? SizeBytes,
    int MessageId, string? MessageSubject, string? MessageFrom, string? MessageDate);

public class AttachmentRepository(AppDatabase db)
{
    public void Insert(int messageId, string? filename, string? contentType,
        int? sizeBytes, string? contentId, bool isInline)
    {
        db.ExecuteWrite(conn =>
        {
            // Skip if this attachment already exists for the message (prevents duplicates on re-sync)
            using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT 1 FROM attachments
                WHERE message_id = $messageId
                  AND COALESCE(filename, '') = COALESCE($filename, '')
                  AND COALESCE(content_id, '') = COALESCE($contentId, '')
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$messageId", messageId);
            check.Parameters.AddWithValue("$filename", (object?)filename ?? DBNull.Value);
            check.Parameters.AddWithValue("$contentId", (object?)contentId ?? DBNull.Value);
            if (check.ExecuteScalar() is not null) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO attachments (message_id, filename, content_type, size_bytes, content_id, is_inline)
                VALUES ($messageId, $filename, $contentType, $sizeBytes, $contentId, $isInline);
                """;
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$filename", (object?)filename ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contentType", (object?)contentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sizeBytes", (object?)sizeBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contentId", (object?)contentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isInline", isInline ? 1 : 0);
            cmd.ExecuteNonQuery();
        });
    }

    public List<AttachmentRecord> GetByMessageId(int messageId)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM attachments WHERE message_id = $messageId;";
        cmd.Parameters.AddWithValue("$messageId", messageId);
        using var reader = cmd.ExecuteReader();
        var list = new List<AttachmentRecord>();
        while (reader.Read())
            list.Add(ReadRecord(reader));
        return list;
    }

    /// <summary>Get a single attachment by its primary key.</summary>
    public AttachmentRecord? GetById(int id)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM attachments WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>
    /// Search attachments across messages with flexible filters.
    /// Returns results joined with parent message metadata.
    /// </summary>
    public List<AttachmentSearchResult> Search(
        string? accountId = null, string? filename = null, string? contentType = null,
        long? fromDateEpoch = null, long? toDateEpoch = null,
        int? minSize = null, int? maxSize = null, int maxResults = 50)
    {
        using var conn = db.GetReadConnection();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (accountId is not null) conditions.Add("m.account_id = $accountId");
        if (filename is not null) conditions.Add("a.filename LIKE $filename ESCAPE '\\'");
        if (contentType is not null) conditions.Add("a.content_type LIKE $contentType ESCAPE '\\'");
        if (fromDateEpoch is not null) conditions.Add("m.date_epoch >= $fromDate");
        if (toDateEpoch is not null) conditions.Add("m.date_epoch <= $toDate");
        if (minSize is not null) conditions.Add("a.size_bytes >= $minSize");
        if (maxSize is not null) conditions.Add("a.size_bytes <= $maxSize");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT a.id as attachment_id, a.filename, a.content_type, a.size_bytes,
                   m.id as message_id, m.subject, m.from_address, m.date
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            {where}
            ORDER BY m.date_epoch DESC
            LIMIT $limit;
            """;

        if (accountId is not null) cmd.Parameters.AddWithValue("$accountId", accountId);
        if (filename is not null) cmd.Parameters.AddWithValue("$filename", $"%{EscapeLike(filename)}%");
        if (contentType is not null) cmd.Parameters.AddWithValue("$contentType", $"%{EscapeLike(contentType)}%");
        if (fromDateEpoch is not null) cmd.Parameters.AddWithValue("$fromDate", fromDateEpoch.Value);
        if (toDateEpoch is not null) cmd.Parameters.AddWithValue("$toDate", toDateEpoch.Value);
        if (minSize is not null) cmd.Parameters.AddWithValue("$minSize", minSize.Value);
        if (maxSize is not null) cmd.Parameters.AddWithValue("$maxSize", maxSize.Value);
        cmd.Parameters.AddWithValue("$limit", maxResults);

        using var reader = cmd.ExecuteReader();
        var list = new List<AttachmentSearchResult>();
        while (reader.Read())
        {
            list.Add(new AttachmentSearchResult(
                AttachmentId: reader.GetInt32(reader.GetOrdinal("attachment_id")),
                Filename: reader.IsDBNull(reader.GetOrdinal("filename")) ? null : reader.GetString(reader.GetOrdinal("filename")),
                ContentType: reader.IsDBNull(reader.GetOrdinal("content_type")) ? null : reader.GetString(reader.GetOrdinal("content_type")),
                SizeBytes: reader.IsDBNull(reader.GetOrdinal("size_bytes")) ? null : reader.GetInt32(reader.GetOrdinal("size_bytes")),
                MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                MessageSubject: reader.IsDBNull(reader.GetOrdinal("subject")) ? null : reader.GetString(reader.GetOrdinal("subject")),
                MessageFrom: reader.IsDBNull(reader.GetOrdinal("from_address")) ? null : reader.GetString(reader.GetOrdinal("from_address")),
                MessageDate: reader.IsDBNull(reader.GetOrdinal("date")) ? null : reader.GetString(reader.GetOrdinal("date"))
            ));
        }
        return list;
    }

    /// <summary>Update the local_path and downloaded_at after downloading an attachment.</summary>
    public void UpdateDownloadPath(int attachmentId, string localPath)
    {
        db.ExecuteWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE attachments SET local_path = $path, downloaded_at = datetime('now')
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", attachmentId);
            cmd.Parameters.AddWithValue("$path", localPath);
            cmd.ExecuteNonQuery();
        });
    }

    private static AttachmentRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Id: reader.GetInt32(reader.GetOrdinal("id")),
        MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
        Filename: reader.IsDBNull(reader.GetOrdinal("filename")) ? null : reader.GetString(reader.GetOrdinal("filename")),
        ContentType: reader.IsDBNull(reader.GetOrdinal("content_type")) ? null : reader.GetString(reader.GetOrdinal("content_type")),
        SizeBytes: reader.IsDBNull(reader.GetOrdinal("size_bytes")) ? null : reader.GetInt32(reader.GetOrdinal("size_bytes")),
        ContentId: reader.IsDBNull(reader.GetOrdinal("content_id")) ? null : reader.GetString(reader.GetOrdinal("content_id")),
        IsInline: reader.GetInt32(reader.GetOrdinal("is_inline")) == 1,
        LocalPath: reader.IsDBNull(reader.GetOrdinal("local_path")) ? null : reader.GetString(reader.GetOrdinal("local_path")),
        DownloadedAt: reader.IsDBNull(reader.GetOrdinal("downloaded_at")) ? null : reader.GetString(reader.GetOrdinal("downloaded_at"))
    );

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
