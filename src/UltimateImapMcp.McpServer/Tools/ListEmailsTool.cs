using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class ListEmailsTool(MessageRepository messageRepo, FolderRepository folderRepo)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "List emails in a folder sorted by date (newest first by default). " +
        "Use this to browse a mailbox without searching. " +
        "For keyword-based search, use search_emails instead.")]
    public string ListEmails(
        [Description("Account ID")] string accountId,
        [Description("Folder path (default: INBOX)")] string folderPath = "INBOX",
        [Description("Number of emails to return (default: 20, max: 100)")] int limit = 20,
        [Description("Offset for pagination (default: 0)")] int offset = 0,
        [Description("Sort order: date_desc (default), date_asc, from, subject")] string orderBy = "date_desc")
    {
        if (limit < 1) limit = 1;
        if (limit > 100) limit = 100;
        if (offset < 0) offset = 0;

        var folder = folderRepo.GetByPath(accountId, folderPath);
        if (folder is null)
            return JsonSerializer.Serialize(
                new { error = $"Folder '{folderPath}' not found for account '{accountId}'." },
                JsonOptions);

        var messages = messageRepo.SearchAdvanced(new SearchFilter
        {
            AccountId = accountId,
            FolderId = folder.Id,
            OrderBy = orderBy,
            MaxResults = limit,
            Offset = offset,
        });

        var mapped = messages.Select(m => new
        {
            id = m.Id,
            uid = m.Uid,
            subject = m.Subject,
            from = m.FromAddress,
            date = m.Date,
            flags = m.Flags,
            snippet = m.Snippet,
            has_attachments = m.HasAttachments,
            thread_id = m.ThreadId
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            folder = folderPath,
            count = mapped.Count,
            offset,
            order_by = orderBy,
            results = mapped
        }, JsonOptions);
    }
}
