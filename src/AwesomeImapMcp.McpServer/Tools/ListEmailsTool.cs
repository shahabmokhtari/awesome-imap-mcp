using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class ListEmailsTool(MessageRepository messageRepo, FolderRepository folderRepo, AppConfig config, ILogger<ListEmailsTool> logger)
{
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
        return McpJsonDefaults.LogToolCall(logger, "list_emails",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["folderPath"] = folderPath, ["limit"] = limit, ["offset"] = offset },
            () =>
            {
                try
                {
                    if (limit < 1) limit = 1;
                    if (limit > 100) limit = 100;
                    if (offset < 0) offset = 0;

                    var folder = folderRepo.GetByPath(accountId, folderPath);
                    if (folder is null)
                        return McpJsonDefaults.Error($"Folder '{folderPath}' not found for account '{accountId}'.");

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

                    var backfillDone = folder.OldestSyncedUid <= 1;

                    return JsonSerializer.Serialize(new
                    {
                        folder = folderPath,
                        count = mapped.Count,
                        offset,
                        order_by = orderBy,
                        results = mapped,
                        cache_info = new
                        {
                            source = "cache",
                            backfill_complete = backfillDone,
                            total_on_server = folder.MessageCount,
                            cached_in_results = mapped.Count,
                            hint = backfillDone
                                ? (string?)null
                                : "Only recently synced messages shown. Older messages are being backfilled. Use search_emails with server_search=true for complete results."
                        }
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ListEmails failed");
                    return McpJsonDefaults.Error(ex.Message);
                }
            }, config);
    }
}
