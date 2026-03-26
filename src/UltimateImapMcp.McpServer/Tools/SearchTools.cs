using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.McpServer.Tools;

[McpServerToolType]
public class SearchTools(MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description(
        "Search emails with flexible filters. Searches local cache by default. " +
        "Use server_search=true to search directly on the IMAP server (slower but searches all mail).")]
    public async Task<string> SearchEmails(
        [Description("Search query text (FTS match for local, IMAP SEARCH for server)")] string? query = null,
        [Description("Account ID (required for server_search)")] string? accountId = null,
        [Description("Folder path to search in (e.g., INBOX)")] string? folder = null,
        [Description("Filter by sender email address")] string? from = null,
        [Description("Filter by recipient email address")] string? to = null,
        [Description("Filter by subject (substring match)")] string? subject = null,
        [Description("Start date (ISO 8601, e.g., 2026-01-01)")] string? fromDate = null,
        [Description("End date (ISO 8601, e.g., 2026-03-25)")] string? toDate = null,
        [Description("Sort order: date_desc (default), date_asc, from, subject, size_desc")] string order = "date_desc",
        [Description("Max results (default 20)")] int maxResults = 20,
        [Description("Summary only (default: true)")] bool summaryOnly = true,
        [Description("Search on IMAP server instead of local cache (default: false)")] bool serverSearch = false,
        [Description("Max body length when summary_only=false (0=unlimited)")] int maxBodyLength = 0)
    {
        try
        {
            // Parse dates to epoch
            long? fromEpoch = null, toEpoch = null;
            if (fromDate is not null && DateTimeOffset.TryParse(fromDate, out var fd))
                fromEpoch = fd.ToUnixTimeSeconds();
            if (toDate is not null && DateTimeOffset.TryParse(toDate, out var td))
                toEpoch = td.ToUnixTimeSeconds();

            // Resolve folder ID if folder path provided
            int? folderId = null;
            if (folder is not null && accountId is not null)
            {
                var folderRecord = folderRepo.GetByPath(accountId, folder);
                folderId = folderRecord?.Id;
            }

            List<MessageRecord> results;

            if (serverSearch)
            {
                if (string.IsNullOrEmpty(accountId))
                    return Error("account_id is required for server_search.");

                results = await syncManager.ServerSearchAsync(
                    accountId, folder ?? "INBOX", query, from, to, subject,
                    fromEpoch, toEpoch, maxResults).ConfigureAwait(false);
            }
            else
            {
                results = messageRepo.SearchAdvanced(new SearchFilter
                {
                    Query = query,
                    AccountId = accountId,
                    FolderId = folderId,
                    FromAddress = from,
                    ToAddress = to,
                    Subject = subject,
                    FromDateEpoch = fromEpoch,
                    ToDateEpoch = toEpoch,
                    OrderBy = order,
                    MaxResults = maxResults,
                });
            }

            var mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();

            return JsonSerializer.Serialize(new
            {
                count = mapped.Count,
                source = serverSearch ? "server" : "cache",
                results = mapped,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return Error($"Search failed: {ex.Message}");
        }
    }

    private static object FormatMessage(MessageRecord m, bool summaryOnly, int maxBodyLength)
    {
        if (summaryOnly)
        {
            return new
            {
                id = m.Id, uid = m.Uid, folder_id = m.FolderId,
                subject = m.Subject, from = m.FromAddress,
                date = m.Date, snippet = m.Snippet,
                has_attachments = m.HasAttachments, thread_id = m.ThreadId
            };
        }

        var body = m.BodyText;
        if (maxBodyLength > 0 && body is not null && body.Length > maxBodyLength)
            body = body[..maxBodyLength] + "... [truncated]";

        return new
        {
            id = m.Id, uid = m.Uid, folder_id = m.FolderId,
            subject = m.Subject, from = m.FromAddress,
            to = m.ToAddresses, cc = m.CcAddresses,
            date = m.Date, body, body_fetched = m.BodyFetched,
            snippet = m.Snippet, has_attachments = m.HasAttachments,
            thread_id = m.ThreadId
        };
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
