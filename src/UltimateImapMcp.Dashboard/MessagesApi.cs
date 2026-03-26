using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

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

            var messages = messageRepo.GetByFolder(accountId, folderId, limit);

            // Look up the folder path for context
            var folders = folderRepo.GetByAccount(accountId);
            var folderPath = folders.FirstOrDefault(f => f.Id == folderId)?.Path ?? "";

            var result = messages.Select(m => new
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
                folderPath,
            });

            return Results.Ok(result);
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
            });
        });

        // POST /api/messages/{accountId}/{folderId}/{uid}/fetch-body — Fetch message body on demand
        app.MapPost("/api/messages/{accountId}/{folderId:int}/{uid:long}/fetch-body", async (
            string accountId, int folderId, long uid,
            UltimateImapMcp.Core.Email.IEmailBackendFactory backendFactory,
            UltimateImapMcp.ImapClient.Repositories.FolderRepository folderRepo,
            UltimateImapMcp.ImapClient.Repositories.MessageRepository messageRepo,
            ILoggerFactory loggerFactory) =>
        {
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

        // GET /api/messages/search?account_id=X&query=text&limit=20 — Full-text search
        app.MapGet("/api/messages/search", (HttpContext ctx, MessageRepository messageRepo, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            var query = ctx.Request.Query["query"].FirstOrDefault();
            if (string.IsNullOrEmpty(query))
                return Results.BadRequest(new { error = "query is required" });

            var limitStr = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = 20;
            if (!string.IsNullOrEmpty(limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 100);

            var folderIdStr = ctx.Request.Query["folder_id"].FirstOrDefault();
            int? folderId = null;
            if (!string.IsNullOrEmpty(folderIdStr) && int.TryParse(folderIdStr, out var parsedFolderId))
                folderId = parsedFolderId;

            var messages = messageRepo.SearchFts(query, accountId, folderId, limit);

            // Build a folder ID -> path lookup
            Dictionary<int, string> folderPaths = new();
            if (!string.IsNullOrEmpty(accountId))
            {
                var folders = folderRepo.GetByAccount(accountId);
                foreach (var f in folders)
                    folderPaths[f.Id] = f.Path;
            }

            var result = messages.Select(m => new
            {
                id = m.Id,
                uid = m.Uid,
                folderId = m.FolderId,
                subject = m.Subject ?? "(no subject)",
                fromAddress = m.FromAddress ?? "",
                fromEmail = m.FromEmail ?? "",
                dateEpoch = m.DateEpoch,
                date = m.Date,
                flags = m.Flags ?? "",
                snippet = m.Snippet ?? "",
                hasAttachments = m.HasAttachments,
                folderPath = folderPaths.TryGetValue(m.FolderId, out var path) ? path : "",
            });

            return Results.Ok(result);
        });

        return app;
    }
}
