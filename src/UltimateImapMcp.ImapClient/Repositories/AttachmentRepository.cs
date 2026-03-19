using UltimateImapMcp.Core.Database;

namespace UltimateImapMcp.ImapClient.Repositories;

public record AttachmentRecord(
    int Id, int MessageId, string? Filename, string? ContentType,
    int? SizeBytes, string? ContentId, bool IsInline,
    string? LocalPath, string? DownloadedAt);

public class AttachmentRepository(AppDatabase db)
{
    public void Insert(int messageId, string? filename, string? contentType,
        int? sizeBytes, string? contentId, bool isInline)
    {
        db.ExecuteWrite(conn =>
        {
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
            list.Add(new AttachmentRecord(
                Id: reader.GetInt32(reader.GetOrdinal("id")),
                MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                Filename: reader.IsDBNull(reader.GetOrdinal("filename")) ? null : reader.GetString(reader.GetOrdinal("filename")),
                ContentType: reader.IsDBNull(reader.GetOrdinal("content_type")) ? null : reader.GetString(reader.GetOrdinal("content_type")),
                SizeBytes: reader.IsDBNull(reader.GetOrdinal("size_bytes")) ? null : reader.GetInt32(reader.GetOrdinal("size_bytes")),
                ContentId: reader.IsDBNull(reader.GetOrdinal("content_id")) ? null : reader.GetString(reader.GetOrdinal("content_id")),
                IsInline: reader.GetInt32(reader.GetOrdinal("is_inline")) == 1,
                LocalPath: reader.IsDBNull(reader.GetOrdinal("local_path")) ? null : reader.GetString(reader.GetOrdinal("local_path")),
                DownloadedAt: reader.IsDBNull(reader.GetOrdinal("downloaded_at")) ? null : reader.GetString(reader.GetOrdinal("downloaded_at"))
            ));
        return list;
    }
}
