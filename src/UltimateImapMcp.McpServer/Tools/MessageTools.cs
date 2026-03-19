using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class MessageTools(MessageRepository messageRepo, AttachmentRepository attachmentRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Get a single email message by account, folder, and UID. " +
        "Returns full message content including body if cached.")]
    public string GetMessage(
        [Description("Account ID")] string accountId,
        [Description("Folder ID (integer)")] int folderId,
        [Description("Message UID")] int uid,
        [Description("Max body length (0=unlimited, default: 0)")] int maxBodyLength = 0)
    {
        var msg = messageRepo.GetByUid(accountId, folderId, uid);
        if (msg is null)
            return JsonSerializer.Serialize(
                new { error = $"Message UID {uid} not found in folder {folderId} for account '{accountId}'." },
                JsonOptions);

        var attachments = attachmentRepo.GetByMessageId(msg.Id);

        var body = msg.BodyText;
        if (maxBodyLength > 0 && body != null && body.Length > maxBodyLength)
            body = body[..maxBodyLength];

        return JsonSerializer.Serialize(new
        {
            id = msg.Id,
            uid = msg.Uid,
            account_id = msg.AccountId,
            folder_id = msg.FolderId,
            message_id = msg.MessageId,
            thread_id = msg.ThreadId,
            subject = msg.Subject,
            from = msg.FromAddress,
            to = msg.ToAddresses,
            cc = msg.CcAddresses,
            bcc = msg.BccAddresses,
            date = msg.Date,
            flags = msg.Flags,
            size_bytes = msg.SizeBytes,
            has_attachments = msg.HasAttachments,
            body_fetched = msg.BodyFetched,
            body = body,
            snippet = msg.Snippet,
            attachments = attachments.Select(a => new
            {
                id = a.Id,
                filename = a.Filename,
                content_type = a.ContentType,
                size_bytes = a.SizeBytes,
                is_inline = a.IsInline,
                content_id = a.ContentId,
                local_path = a.LocalPath,
                downloaded_at = a.DownloadedAt
            }).ToList()
        }, JsonOptions);
    }

    [McpServerTool, Description(
        "Get all messages in a conversation thread, ordered by date.")]
    public string GetThread(
        [Description("Thread ID (hash)")] string threadId,
        [Description("Summary only (default: false)")] bool summaryOnly = false,
        [Description("Max body length in characters (0=unlimited, default: 0). Applied when summary_only=false.")] int maxBodyLength = 0)
    {
        var messages = messageRepo.GetByThreadId(threadId);

        var mapped = messages.Select(m =>
        {
            if (summaryOnly)
            {
                return (object)new
                {
                    uid = m.Uid,
                    subject = m.Subject,
                    from = m.FromAddress,
                    date = m.Date,
                    snippet = m.Snippet
                };
            }

            var body = m.BodyText;
            if (maxBodyLength > 0 && body != null && body.Length > maxBodyLength)
                body = body[..maxBodyLength] + "... [truncated]";

            return (object)new
            {
                uid = m.Uid,
                subject = m.Subject,
                from = m.FromAddress,
                to = m.ToAddresses,
                date = m.Date,
                body,
                body_fetched = m.BodyFetched,
                snippet = m.Snippet
            };
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            thread_id = threadId,
            message_count = mapped.Count,
            messages = mapped
        }, JsonOptions);
    }
}
