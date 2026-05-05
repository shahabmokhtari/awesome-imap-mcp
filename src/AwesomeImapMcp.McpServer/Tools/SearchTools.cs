using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Email;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.McpServer.Tools;

[McpServerToolType]
public class SearchTools(MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager, IEmailBackendFactory backendFactory, AppConfig config, ILogger<SearchTools> logger)
{
    [McpServerTool, Description(
        "Search emails with flexible filters. Searches local cache by default. " +
        "Use server_search=true to search directly on the IMAP server (slower but searches all mail). " +
        "Combine query, from, to, subject, and date filters to narrow results.")]
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
        [Description("Offset for pagination (default: 0)")] int offset = 0,
        [Description("Summary only (default: true)")] bool summaryOnly = true,
        [Description("Search on IMAP server instead of local cache (default: false)")] bool serverSearch = false,
        [Description("Max body length when summary_only=false (0=unlimited)")] int maxBodyLength = 0,
        [Description("Auto-fetch bodies for results before returning (default: false)")] bool fetchBodies = false)
    {
        return await McpJsonDefaults.LogToolCallAsync(logger, "search_emails",
            new Dictionary<string, object?> { ["query"] = query, ["accountId"] = accountId, ["folder"] = folder, ["serverSearch"] = serverSearch },
            async () =>
            {
                try
                {
                    // Parse dates to epoch
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
                            return McpJsonDefaults.Error("account_id is required for server_search.");

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
                            Offset = offset,
                        });
                    }

                    var mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();

                    var bodiesFetched = 0;
                    if (fetchBodies && results.Count > 0)
                    {
                        var groups = results
                            .Where(m => !m.BodyFetched)
                            .GroupBy(m => (m.AccountId, m.FolderId));

                        foreach (var group in groups)
                        {
                            try
                            {
                                var folderRecord = folderRepo.GetByAccount(group.Key.AccountId)
                                    .FirstOrDefault(f => f.Id == group.Key.FolderId);
                                if (folderRecord is null) continue;

                                var groupUids = group.Select(m => (long)m.Uid).ToList();
                                await using var backend = backendFactory.CreateSyncBackend(group.Key.AccountId);
                                bodiesFetched += await backend.FetchMessageBodiesBatchAsync(
                                    group.Key.AccountId, folderRecord.Path, groupUids).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Failed to batch-fetch bodies for search results");
                            }
                        }

                        if (bodiesFetched > 0)
                        {
                            results = results.Select(m =>
                            {
                                if (!m.BodyFetched)
                                {
                                    var refreshed = messageRepo.GetById(m.Id);
                                    return refreshed ?? m;
                                }
                                return m;
                            }).ToList();
                            mapped = results.Select(m => FormatMessage(m, summaryOnly, maxBodyLength)).ToList();
                        }
                    }

                    // Build cache info for local cache searches
                    object? cacheInfo = null;
                    if (serverSearch)
                    {
                        cacheInfo = new { source = "server" };
                    }
                    else
                    {
                        FolderRecord? targetFolder = null;
                        if (accountId is not null)
                        {
                            targetFolder = folderId is not null
                                ? folderRepo.GetByAccount(accountId).FirstOrDefault(f => f.Id == folderId)
                                : folder is not null
                                    ? folderRepo.GetByPath(accountId, folder)
                                    : folderRepo.GetByAccount(accountId).FirstOrDefault(f => f.Role == "inbox");
                        }

                        var backfillDone = targetFolder is not null && targetFolder.OldestSyncedUid <= 1;
                        cacheInfo = new
                        {
                            source = "cache",
                            backfill_complete = backfillDone,
                            total_on_server = targetFolder?.MessageCount ?? 0,
                            cached_in_results = mapped.Count,
                            hint = backfillDone
                                ? (string?)null
                                : "Older messages may exist on server. Use server_search=true or wait for backfill sync to complete."
                        };
                    }

                    return JsonSerializer.Serialize(new
                    {
                        count = mapped.Count,
                        source = serverSearch ? "server" : "cache",
                        bodies_fetched = fetchBodies ? bodiesFetched : (int?)null,
                        results = mapped,
                        cache_info = cacheInfo,
                    }, McpJsonDefaults.Options);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return McpJsonDefaults.Error($"Search failed: {ex.Message}");
                }
            }, config);
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
}
