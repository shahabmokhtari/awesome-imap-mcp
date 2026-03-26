using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class MessageTools(
    MessageRepository messageRepo,
    AttachmentRepository attachmentRepo,
    FolderRepository folderRepo,
    IEmailBackendFactory backendFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Get a single email message with full details including body and attachments. " +
        "Provide either 'messageId' (database ID) alone, or 'accountId' + 'uid' (with optional 'folderId'). " +
        "Automatically fetches message body from server if not yet cached (unless fetchBody=false).")]
    public async Task<string> GetMessage(
        [Description("Database message ID (if provided, other ID params are ignored)")] int? messageId = null,
        [Description("Account ID")] string? accountId = null,
        [Description("Folder ID (integer, optional if messageId is provided)")] int? folderId = null,
        [Description("Message UID")] int? uid = null,
        [Description("Max body length (0=unlimited, default: 0)")] int maxBodyLength = 0,
        [Description("Fetch body from server if not cached (default: true). Set false for metadata only.")] bool fetchBody = true)
    {
        var msg = messageRepo.Resolve(messageId, accountId, folderId, uid, folderRepo);
        if (msg is null)
            return Error("Message not found. Provide 'messageId' or 'accountId'+'uid'.");

        // Auto-fetch body if not yet cached and fetchBody is enabled
        if (!msg.BodyFetched && fetchBody)
        {
            try
            {
                var folder = folderRepo.GetByAccount(msg.AccountId)
                    .FirstOrDefault(f => f.Id == msg.FolderId);
                if (folder is not null)
                {
                    await using var backend = backendFactory.CreateSyncBackend(msg.AccountId);
                    await backend.FetchMessageBodyAsync(msg.AccountId, folder.Path, msg.Uid).ConfigureAwait(false);
                    // Re-read from DB after fetch
                    msg = messageRepo.GetById(msg.Id) ?? msg;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Non-fatal — return what we have with a note
                return JsonSerializer.Serialize(new
                {
                    id = msg.Id, uid = msg.Uid,
                    account_id = msg.AccountId, folder_id = msg.FolderId,
                    subject = msg.Subject, from = msg.FromAddress,
                    body_fetched = false,
                    body_fetch_error = ex.Message,
                    snippet = msg.Snippet
                }, JsonOptions);
            }
        }

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
        "Get all messages in a conversation thread, ordered by date. " +
        "Returns summary by default; set summary_only=false to include full message bodies.")]
    public string GetThread(
        [Description("Thread ID (hash)")] string threadId,
        [Description("Summary only — omits body text (default: true)")] bool summaryOnly = true,
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

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
