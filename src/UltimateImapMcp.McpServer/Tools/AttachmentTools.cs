using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class AttachmentTools(
    AttachmentRepository attachmentRepo,
    MessageRepository messageRepo,
    FolderRepository folderRepo,
    IEmailBackendFactory backendFactory,
    AppConfig config,
    ILogger<AttachmentTools> logger)
{
    [McpServerTool, Description(
        "List all attachments for a specific email message. " +
        "Provide either 'messageId' (database ID) alone, or 'accountId' + 'uid' (with optional 'folderId').")]
    public string ListAttachments(
        [Description("Database message ID (preferred)")] int? messageId = null,
        [Description("Account ID")] string? accountId = null,
        [Description("Folder ID")] int? folderId = null,
        [Description("Message UID")] int? uid = null)
    {
        return McpJsonDefaults.LogToolCall(logger, "list_attachments",
            new Dictionary<string, object?> { ["messageId"] = messageId, ["accountId"] = accountId, ["uid"] = uid },
            () =>
            {
                var msg = messageRepo.Resolve(messageId, accountId, folderId, uid, folderRepo);
                if (msg is null)
                    return McpJsonDefaults.Error("Message not found. Provide 'messageId' or 'accountId'+'uid'.");

                var attachments = attachmentRepo.GetByMessageId(msg.Id);

                return JsonSerializer.Serialize(new
                {
                    message_id = msg.Id,
                    subject = msg.Subject,
                    count = attachments.Count,
                    attachments = attachments.Select(a => new
                    {
                        attachment_id = a.Id,
                        filename = a.Filename,
                        content_type = a.ContentType,
                        size_bytes = a.SizeBytes,
                        is_inline = a.IsInline,
                        content_id = a.ContentId,
                        downloaded = a.DownloadedAt is not null,
                        local_path = a.LocalPath
                    }).ToList()
                }, McpJsonDefaults.Options);
            }, config);
    }

    [McpServerTool, Description(
        "Search for attachments across all messages with flexible filters. " +
        "Returns attachment info along with parent message context (subject, sender, date).")]
    public string SearchAttachments(
        [Description("Account ID (optional — searches all accounts if omitted)")] string? accountId = null,
        [Description("Filename substring match")] string? filename = null,
        [Description("Content type substring match (e.g., 'pdf', 'image/')")] string? contentType = null,
        [Description("Start date (ISO 8601, e.g., 2026-01-01)")] string? fromDate = null,
        [Description("End date (ISO 8601, e.g., 2026-03-25)")] string? toDate = null,
        [Description("Minimum size in bytes")] int? minSize = null,
        [Description("Maximum size in bytes")] int? maxSize = null,
        [Description("Max results (default 50)")] int maxResults = 50)
    {
        return McpJsonDefaults.LogToolCall(logger, "search_attachments",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["filename"] = filename, ["contentType"] = contentType },
            () =>
            {
                try
                {
                    long? fromEpoch = null, toEpoch = null;
                    if (fromDate is not null)
                    {
                        if (!DateTimeOffset.TryParse(fromDate, out var fd))
                            return McpJsonDefaults.Error($"Invalid fromDate format: '{fromDate}'. Use ISO 8601 (e.g., 2026-01-01).");
                        fromEpoch = fd.ToUnixTimeSeconds();
                    }
                    if (toDate is not null)
                    {
                        if (!DateTimeOffset.TryParse(toDate, out var td))
                            return McpJsonDefaults.Error($"Invalid toDate format: '{toDate}'. Use ISO 8601 (e.g., 2026-03-25).");
                        toEpoch = td.ToUnixTimeSeconds();
                    }

                    var results = attachmentRepo.Search(accountId, filename, contentType,
                        fromEpoch, toEpoch, minSize, maxSize, maxResults);

                    return JsonSerializer.Serialize(new
                    {
                        count = results.Count,
                        results = results.Select(r => new
                        {
                            attachment_id = r.AttachmentId,
                            filename = r.Filename,
                            content_type = r.ContentType,
                            size_bytes = r.SizeBytes,
                            message_id = r.MessageId,
                            message_subject = r.MessageSubject,
                            message_from = r.MessageFrom,
                            message_date = r.MessageDate
                        }).ToList()
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Search failed: {ex.Message}");
                }
            }, config);
    }

    [McpServerTool, Description(
        "Get detailed information about a specific attachment by its ID.")]
    public string GetAttachmentInfo(
        [Description("Attachment ID (database primary key)")] int attachmentId)
    {
        return McpJsonDefaults.LogToolCall(logger, "get_attachment_info",
            new Dictionary<string, object?> { ["attachmentId"] = attachmentId },
            () =>
            {
                var attachment = attachmentRepo.GetById(attachmentId);
                if (attachment is null)
                    return McpJsonDefaults.Error($"Attachment with ID {attachmentId} not found.");

                var msg = messageRepo.GetById(attachment.MessageId);

                return JsonSerializer.Serialize(new
                {
                    attachment_id = attachment.Id,
                    filename = attachment.Filename,
                    content_type = attachment.ContentType,
                    size_bytes = attachment.SizeBytes,
                    content_id = attachment.ContentId,
                    is_inline = attachment.IsInline,
                    downloaded = attachment.DownloadedAt is not null,
                    local_path = attachment.LocalPath,
                    downloaded_at = attachment.DownloadedAt,
                    message = msg is null ? null : new
                    {
                        id = msg.Id,
                        uid = msg.Uid,
                        account_id = msg.AccountId,
                        folder_id = msg.FolderId,
                        subject = msg.Subject,
                        from = msg.FromAddress,
                        date = msg.Date
                    }
                }, McpJsonDefaults.Options);
            }, config);
    }

    [McpServerTool, Description(
        "Download an attachment from the IMAP server and save it to disk. " +
        "Fetches the full message from the server and extracts the specific attachment.")]
    public async Task<string> DownloadAttachment(
        [Description("Attachment ID (database primary key)")] int attachmentId,
        [Description("Path where the file should be saved (directory or full path)")] string savePath)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "download_attachment",
            new Dictionary<string, object?> { ["attachmentId"] = attachmentId, ["savePath"] = savePath },
            async () =>
            {
                try
                {
                    var attachment = attachmentRepo.GetById(attachmentId);
                    if (attachment is null)
                        return McpJsonDefaults.Error($"Attachment with ID {attachmentId} not found.");

                    // If already downloaded and file exists, return cached path
                    if (attachment.LocalPath is not null && File.Exists(attachment.LocalPath))
                    {
                        return JsonSerializer.Serialize(new
                        {
                            saved_to = attachment.LocalPath,
                            filename = attachment.Filename,
                            size_bytes = new FileInfo(attachment.LocalPath).Length,
                            source = "cache"
                        }, McpJsonDefaults.Options);
                    }

                    var msg = messageRepo.GetById(attachment.MessageId);
                    if (msg is null)
                        return McpJsonDefaults.Error("Parent message not found in cache.");

                    // Find the folder for this message
                    var folder = folderRepo.GetByAccount(msg.AccountId)
                        .FirstOrDefault(f => f.Id == msg.FolderId);
                    if (folder is null)
                        return McpJsonDefaults.Error($"Folder not found for message (folderId={msg.FolderId}).");

                    // Resolve save path — if it's a directory, append the sanitized filename
                    var resolvedPath = savePath;
                    if (Directory.Exists(savePath) || savePath.EndsWith(Path.DirectorySeparatorChar) || savePath.EndsWith(Path.AltDirectorySeparatorChar))
                    {
                        // Sanitize filename to prevent path traversal from untrusted email data
                        var rawFilename = attachment.Filename ?? $"attachment_{attachmentId}";
                        var filename = Path.GetFileName(rawFilename);
                        if (string.IsNullOrWhiteSpace(filename))
                            filename = $"attachment_{attachmentId}";
                        resolvedPath = Path.Combine(savePath, filename);
                    }

                    // Verify resolved path is within the intended directory
                    var fullResolved = Path.GetFullPath(resolvedPath);
                    var fullSaveDir = Path.GetFullPath(Directory.Exists(savePath) ? savePath : Path.GetDirectoryName(savePath) ?? savePath);
                    if (!fullResolved.StartsWith(fullSaveDir, StringComparison.Ordinal))
                        return McpJsonDefaults.Error("Resolved path is outside the target directory.");

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(resolvedPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    // Download via the email backend
                    await using var backend = backendFactory.CreateSyncBackend(msg.AccountId);
                    var bytesWritten = await backend.DownloadAttachmentAsync(
                        msg.AccountId, folder.Path, msg.Uid,
                        attachment.Filename, attachment.ContentId,
                        resolvedPath).ConfigureAwait(false);

                    // Update the attachment record with the download path
                    try
                    {
                        attachmentRepo.UpdateDownloadPath(attachmentId, resolvedPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to update download path in DB");
                    }

                    return JsonSerializer.Serialize(new
                    {
                        saved_to = resolvedPath,
                        filename = attachment.Filename,
                        size_bytes = bytesWritten,
                        source = "server"
                    }, McpJsonDefaults.Options);
                }
                catch (NotSupportedException ex)
                {
                    return McpJsonDefaults.Error(ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Download failed: {ex.Message}");
                }
            }, config);
    }
}
