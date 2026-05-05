using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.ImapClient.Repositories;
using AwesomeImapMcp.Queue;
using AwesomeImapMcp.Queue.Models;

namespace AwesomeImapMcp.Dashboard;

public static class MessagesApi
{
    public static IEndpointRouteBuilder MapMessagesApi(this IEndpointRouteBuilder app)
    {
        // GET /api/folders?account_id=X — List folders for an account
        app.MapGet("/api/folders", (HttpContext ctx, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(accountId))
                return Results.BadRequest(new { error = "account_id is required" });

            var folders = folderRepo.GetByAccount(accountId);
            var result = folders.Select(f => new
            {
                id = f.Id,
                path = f.Path,
                displayName = f.DisplayName ?? f.Path,
                role = f.Role,
                messageCount = f.MessageCount,
                unreadCount = f.UnreadCount,
                syncEnabled = f.SyncEnabled,
                lastSyncedAt = f.LastSyncedAt,
            });

            return Results.Ok(result);
        });

        // GET /api/messages?account_id=X&folder_id=Y&limit=50&offset=0 — List messages in a folder
        app.MapGet("/api/messages", (HttpContext ctx, MessageRepository messageRepo, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(accountId))
                return Results.BadRequest(new { error = "account_id is required" });

            var folderIdStr = ctx.Request.Query["folder_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(folderIdStr) || !int.TryParse(folderIdStr, out var folderId))
                return Results.BadRequest(new { error = "folder_id is required and must be an integer" });

            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = 50;
            if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 500);

            var offsetStr = ctx.Request.Query["offset"].FirstOrDefault();
            var offset = 0;
            if (!string.IsNullOrEmpty(offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                offset = Math.Max(0, parsedOffset);

            var messages = messageRepo.GetByFolder(accountId, folderId, limit, offset);
            var totalCount = messageRepo.CountByFolder(accountId, folderId);

            // Look up the folder path for context
            var folders = folderRepo.GetByAccount(accountId);
            var folderPath = folders.FirstOrDefault(f => f.Id == folderId)?.Path ?? "";

            return Results.Ok(new
            {
                totalCount,
                messages = messages.Select(m => new
                {
                    id = m.Id,
                    uid = m.Uid,
                    subject = m.Subject ?? "(no subject)",
                    fromAddress = m.FromAddress ?? "",
                    fromEmail = m.FromEmail ?? "",
                    dateEpoch = m.DateEpoch,
                    date = m.Date,
                    flags = m.Flags ?? "",
                    snippet = m.Snippet ?? "",
                    hasAttachments = m.HasAttachments,
                    bodyFetched = m.BodyFetched,
                    folderPath,
                }).ToList()
            });
        });

        // GET /api/messages/{accountId}/{folderId}/{uid} — Get a single message with full body
        app.MapGet("/api/messages/{accountId}/{folderId:int}/{uid:long}", (
            string accountId, int folderId, long uid, MessageRepository messageRepo) =>
        {
            var message = messageRepo.GetByUid(accountId, folderId, uid);
            if (message is null)
                return Results.NotFound(new { error = "Message not found" });

            return Results.Ok(new
            {
                id = message.Id,
                uid = message.Uid,
                subject = message.Subject ?? "(no subject)",
                fromAddress = message.FromAddress ?? "",
                fromEmail = message.FromEmail ?? "",
                toAddresses = message.ToAddresses ?? "",
                ccAddresses = message.CcAddresses ?? "",
                dateEpoch = message.DateEpoch,
                date = message.Date,
                flags = message.Flags ?? "",
                snippet = message.Snippet ?? "",
                hasAttachments = message.HasAttachments,
                bodyText = message.BodyText,
                bodyHtml = message.BodyHtml,
                bodyFetched = message.BodyFetched,
                threadId = message.ThreadId,
                messageId = message.MessageId,
                inReplyTo = message.InReplyTo,
                referencesHdr = message.ReferencesHdr,
                sizeBytes = message.SizeBytes,
                rawHeaders = message.RawHeaders,
            });
        });

        // POST /api/messages/{accountId}/{folderId}/{uid}/fetch-body — Fetch message body on demand
        app.MapPost("/api/messages/{accountId}/{folderId:int}/{uid:long}/fetch-body", async (
            string accountId, int folderId, long uid,
            AwesomeImapMcp.Core.Email.IEmailBackendFactory backendFactory,
            AwesomeImapMcp.ImapClient.Repositories.FolderRepository folderRepo,
            AwesomeImapMcp.ImapClient.Repositories.MessageRepository messageRepo,
            ILoggerFactory loggerFactory) =>
        {
            // Check if body is already cached — return immediately without creating a backend
            var cached = messageRepo.GetByUid(accountId, folderId, uid);
            if (cached is not null && cached.BodyFetched)
            {
                return Results.Ok(new
                {
                    id = cached.Id,
                    uid = cached.Uid,
                    subject = cached.Subject ?? "(no subject)",
                    bodyText = cached.BodyText,
                    bodyHtml = cached.BodyHtml,
                    bodyFetched = cached.BodyFetched,
                });
            }

            // Resolve folder path from folderId
            var folders = folderRepo.GetByAccount(accountId);
            var folder = folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null)
                return Results.NotFound(new { error = "Folder not found" });

            try
            {
                await using var backend = backendFactory.CreateSyncBackend(accountId);
                await backend.FetchMessageBodyAsync(accountId, folder.Path, uid).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var logger = loggerFactory.CreateLogger("MessagesApi");
                logger.LogError(ex, "fetch-body failed for account={AccountId} folder={FolderId} uid={Uid}", accountId, folderId, uid);
                return Results.Json(new { error = "Failed to fetch message body. Check server logs for details." }, statusCode: 500);
            }

            // Return updated message (outside try so read-back errors are distinct)
            var message = messageRepo.GetByUid(accountId, folderId, uid);
            if (message is null)
                return Results.NotFound(new { error = "Message not found after fetch" });

            return Results.Ok(new
            {
                id = message.Id,
                uid = message.Uid,
                subject = message.Subject ?? "(no subject)",
                bodyText = message.BodyText,
                bodyHtml = message.BodyHtml,
                bodyFetched = message.BodyFetched,
            });
        });

        // GET /api/messages/search?account_id=X&query=text&limit=50&offset=0 — Advanced search with structured operators
        app.MapGet("/api/messages/search", (HttpContext ctx, MessageRepository messageRepo, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            var query = ctx.Request.Query["query"].FirstOrDefault();
            if (string.IsNullOrEmpty(query))
                return Results.BadRequest(new { error = "query is required" });

            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = 50;
            if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 200);

            var offsetStr = ctx.Request.Query["offset"].FirstOrDefault();
            var offset = 0;
            if (!string.IsNullOrEmpty(offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                offset = Math.Max(0, parsedOffset);

            var folderIdStr = ctx.Request.Query["folder_id"].FirstOrDefault();
            int? folderId = null;
            if (!string.IsNullOrEmpty(folderIdStr) && int.TryParse(folderIdStr, out var parsedFolderId))
                folderId = parsedFolderId;

            var filter = ParseAdvancedQuery(query, accountId, folderId, limit);
            filter = filter with { Offset = offset };

            List<MessageRecord> messages;
            try
            {
                messages = messageRepo.SearchAdvanced(filter);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                return Results.BadRequest(new { error = $"Search query error: {ex.Message}" });
            }

            // Build folder ID -> path lookup (for all relevant accounts)
            Dictionary<int, string> folderPaths = new();
            if (!string.IsNullOrEmpty(accountId))
            {
                foreach (var f in folderRepo.GetByAccount(accountId))
                    folderPaths[f.Id] = f.Path;
            }
            else
            {
                // Cross-account search: collect folder paths for all accounts in results
                var accountIds = messages.Select(m => m.AccountId).Distinct();
                foreach (var aid in accountIds)
                    foreach (var f in folderRepo.GetByAccount(aid))
                        folderPaths.TryAdd(f.Id, f.Path);
            }

            // Build account ID -> name lookup for cross-account results
            var accountRepo = ctx.RequestServices.GetService<AccountRepository>();
            Dictionary<string, string> accountNames = new();
            if (accountRepo is not null)
            {
                foreach (var a in accountRepo.GetAll())
                    accountNames[a.Id] = a.Name;
            }

            var result = messages.Select(m => new
            {
                id = m.Id,
                uid = m.Uid,
                folderId = m.FolderId,
                accountId = m.AccountId,
                accountName = accountNames.TryGetValue(m.AccountId, out var aName) ? aName : null,
                subject = m.Subject ?? "(no subject)",
                fromAddress = m.FromAddress ?? "",
                fromEmail = m.FromEmail ?? "",
                dateEpoch = m.DateEpoch,
                date = m.Date,
                flags = m.Flags ?? "",
                snippet = m.Snippet ?? "",
                hasAttachments = m.HasAttachments,
                bodyFetched = m.BodyFetched,
                folderPath = folderPaths.TryGetValue(m.FolderId, out var path) ? path : "",
            });

            return Results.Ok(result);
        });

        // POST /api/messages/bulk-action — Bulk delete/trash/archive messages
        app.MapPost("/api/messages/bulk-action", (HttpContext ctx,
            MessageRepository messageRepo, FolderRepository folderRepo,
            AccountRepository accountRepo, QueueManager queueManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("MessagesApi");
            try
            {
                var body = ctx.Request.ReadFromJsonAsync<BulkActionRequest>().GetAwaiter().GetResult();
                if (body is null)
                    return Results.BadRequest(new { error = "Request body is required" });

                var action = (body.Action ?? "delete").ToLowerInvariant();
                if (action is not "delete" and not "trash" and not "archive")
                    return Results.BadRequest(new { error = "action must be 'delete', 'trash', or 'archive'" });

                // Collect messages to operate on: either from selectedIds or from search
                var messageBatches = new List<(string AccountId, int FolderId, string FolderPath, List<long> Uids)>();

                if (body.Scope == "search")
                {
                    // Run the search query with a high limit to collect all matching messages
                    if (string.IsNullOrEmpty(body.SearchQuery))
                        return Results.BadRequest(new { error = "searchQuery is required when scope is 'search'" });

                    var maxResults = Math.Clamp(body.MaxResults ?? 10000, 1, 50000);
                    var filter = ParseAdvancedQuery(body.SearchQuery, body.SearchAccountId, null, maxResults);

                    List<MessageRecord> messages;
                    try
                    {
                        messages = messageRepo.SearchAdvanced(filter);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        return Results.BadRequest(new { error = $"Search query error: {ex.Message}" });
                    }

                    // Group by account_id + folder_id
                    var grouped = messages.GroupBy(m => (m.AccountId, m.FolderId));
                    foreach (var group in grouped)
                    {
                        var folders = folderRepo.GetByAccount(group.Key.AccountId);
                        var folder = folders.FirstOrDefault(f => f.Id == group.Key.FolderId);
                        if (folder is null) continue;
                        messageBatches.Add((group.Key.AccountId, group.Key.FolderId, folder.Path, group.Select(m => m.Uid).ToList()));
                    }
                }
                else
                {
                    // scope == "selected" (default)
                    if (body.SelectedIds is null || body.SelectedIds.Count == 0)
                        return Results.BadRequest(new { error = "selectedIds is required when scope is 'selected'" });

                    // Group by account_id + folder_id
                    var grouped = body.SelectedIds.GroupBy(s => (s.AccountId, s.FolderId));
                    foreach (var group in grouped)
                    {
                        var folders = folderRepo.GetByAccount(group.Key.AccountId);
                        var folder = folders.FirstOrDefault(f => f.Id == group.Key.FolderId);
                        if (folder is null) continue;
                        messageBatches.Add((group.Key.AccountId, group.Key.FolderId, folder.Path, group.Select(s => s.Uid).ToList()));
                    }
                }

                var isMove = action is "trash" or "archive";
                var operationIds = new List<string>();
                var totalQueued = 0;
                const int chunkSize = 50;

                foreach (var (acctId, folderId, folderPath, uids) in messageBatches)
                {
                    // Validate account exists and is enabled
                    var account = accountRepo.ResolveEnabledAccount(acctId);
                    if (account is null) continue;

                    // For move operations, find destination folder
                    string? destFolder = null;
                    if (isMove)
                    {
                        var folders = folderRepo.GetByAccount(acctId);
                        destFolder = action == "trash"
                            ? folders.FirstOrDefault(f => f.Role == "trash")?.Path
                              ?? folders.FirstOrDefault(f => f.Path.Contains("Trash", StringComparison.OrdinalIgnoreCase))?.Path
                              ?? "Trash"
                            : folders.FirstOrDefault(f => f.Role == "archive")?.Path
                              ?? folders.FirstOrDefault(f => f.Path.Contains("Archive", StringComparison.OrdinalIgnoreCase))?.Path
                              ?? "Archive";
                    }

                    // Chunk UIDs into batches of 50
                    for (var i = 0; i < uids.Count; i += chunkSize)
                    {
                        var chunk = uids.Skip(i).Take(chunkSize).ToList();

                        object payloadObj;
                        OperationType opType;

                        if (isMove)
                        {
                            opType = chunk.Count > 10 ? OperationType.BulkMove : OperationType.Move;
                            payloadObj = new
                            {
                                folder = folderPath,
                                destination = destFolder,
                                uids = chunk,
                                reason = $"Bulk {action} from dashboard"
                            };
                        }
                        else
                        {
                            opType = chunk.Count > 10 ? OperationType.BulkDelete : OperationType.Delete;
                            payloadObj = new
                            {
                                folder = folderPath,
                                uids = chunk,
                                reason = "Bulk delete from dashboard"
                            };
                        }

                        var payload = JsonSerializer.Serialize(payloadObj);

                        try
                        {
                            var opId = queueManager.EnqueueOperation(acctId, opType, payload);
                            operationIds.Add(opId);
                            totalQueued += chunk.Count;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to enqueue bulk {Action} for account {AccountId} folder {FolderId} chunk starting at {Offset}",
                                action, acctId, folderId, i);
                        }
                    }
                }

                return Results.Ok(new
                {
                    queued = totalQueued,
                    operationIds
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process bulk action request");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        return app;
    }

    private record BulkActionRequest
    {
        public string? Action { get; init; }
        public string? Scope { get; init; }
        public List<SelectedMessageId>? SelectedIds { get; init; }
        public string? SearchQuery { get; init; }
        public string? SearchAccountId { get; init; }
        public int? MaxResults { get; init; }
    }

    private record SelectedMessageId
    {
        public string AccountId { get; init; } = "";
        public int FolderId { get; init; }
        public long Uid { get; init; }
    }

    private static SearchFilter ParseAdvancedQuery(string rawQuery, string? accountId, int? folderId, int limit)
    {
        var from = (string?)null;
        var to = (string?)null;
        var subject = (string?)null;
        var label = (string?)null;
        long? fromDateEpoch = null;
        long? toDateEpoch = null;
        bool? hasAttachments = null;
        var ftsTerms = new List<string>();

        // Tokenize: handle quoted strings and key:value pairs
        var tokens = TokenizeQuery(rawQuery);
        foreach (var token in tokens)
        {
            var colonIdx = token.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = token[..colonIdx].ToLowerInvariant();
                var value = token[(colonIdx + 1)..].Trim('"');
                switch (key)
                {
                    case "from":
                        from = value;
                        break;
                    case "to":
                        to = value;
                        break;
                    case "subject":
                        subject = value;
                        break;
                    case "label":
                        label = value;
                        break;
                    case "from-date" or "fromdate" or "after":
                        if (DateTimeOffset.TryParse(value, out var fd))
                            fromDateEpoch = fd.ToUnixTimeSeconds();
                        break;
                    case "to-date" or "todate" or "before":
                        if (DateTimeOffset.TryParse(value, out var td))
                            toDateEpoch = td.ToUnixTimeSeconds();
                        break;
                    case "has" or "hasattachments":
                        hasAttachments = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    default:
                        ftsTerms.Add(token); // Unknown operator, treat as search term
                        break;
                }
            }
            else
            {
                ftsTerms.Add(token);
            }
        }

        var ftsQuery = ftsTerms.Count > 0 ? string.Join(" ", ftsTerms) : null;

        return new SearchFilter
        {
            Query = ftsQuery,
            AccountId = accountId,
            FolderId = folderId,
            FromAddress = from,
            ToAddress = to,
            Subject = subject,
            Label = label,
            FromDateEpoch = fromDateEpoch,
            ToDateEpoch = toDateEpoch,
            HasAttachments = hasAttachments,
            MaxResults = limit,
        };
    }

    /// <summary>
    /// Tokenizes a search query, respecting quoted strings.
    /// "from:john subject:\"hello world\" meeting" -> ["from:john", "subject:hello world", "meeting"]
    /// </summary>
    private static List<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < query.Length)
        {
            // Skip whitespace
            while (i < query.Length && char.IsWhiteSpace(query[i])) i++;
            if (i >= query.Length) break;

            var start = i;

            // Check for key:value where value might be quoted
            var colonIdx = -1;
            while (i < query.Length && !char.IsWhiteSpace(query[i]))
            {
                if (query[i] == ':' && colonIdx < 0) colonIdx = i;
                if (query[i] == '"' && colonIdx >= 0)
                {
                    // Quoted value after colon
                    i++; // skip opening quote
                    var valueStart = i;
                    while (i < query.Length && query[i] != '"') i++;
                    var token = query[start..colonIdx] + ":" + query[valueStart..i];
                    if (i < query.Length) i++; // skip closing quote
                    tokens.Add(token);
                    goto next;
                }
                i++;
            }
            tokens.Add(query[start..i]);
            next:;
        }
        return tokens;
    }
}
