using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
                return Results.BadRequest(new { Error = "account_id is required" });

            var folders = folderRepo.GetByAccount(accountId);
            var result = folders.Select(f => new
            {
                f.Id,
                f.Path,
                DisplayName = f.DisplayName ?? f.Path,
                f.Role,
                f.MessageCount,
                f.UnreadCount,
                f.SyncEnabled,
                f.LastSyncedAt,
            });

            return Results.Ok(result);
        });

        // GET /api/messages?account_id=X&folder_id=Y&limit=50&offset=0 — List messages in a folder
        app.MapGet("/api/messages", (HttpContext ctx, MessageRepository messageRepo, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(accountId))
                return Results.BadRequest(new { Error = "account_id is required" });

            var folderIdStr = ctx.Request.Query["folder_id"].FirstOrDefault();
            if (string.IsNullOrEmpty(folderIdStr) || !int.TryParse(folderIdStr, out var folderId))
                return Results.BadRequest(new { Error = "folder_id is required and must be an integer" });

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
                m.Id,
                m.Uid,
                Subject = m.Subject ?? "(no subject)",
                FromAddress = m.FromAddress ?? "",
                FromEmail = m.FromEmail ?? "",
                m.DateEpoch,
                Date = m.Date,
                Flags = m.Flags ?? "",
                Snippet = m.Snippet ?? "",
                m.HasAttachments,
                FolderPath = folderPath,
            });

            return Results.Ok(result);
        });

        // GET /api/messages/{accountId}/{folderId}/{uid} — Get a single message with full body
        app.MapGet("/api/messages/{accountId}/{folderId:int}/{uid:long}", (
            string accountId, int folderId, long uid, MessageRepository messageRepo) =>
        {
            var message = messageRepo.GetByUid(accountId, folderId, uid);
            if (message is null)
                return Results.NotFound(new { Error = "Message not found" });

            return Results.Ok(new
            {
                message.Id,
                message.Uid,
                Subject = message.Subject ?? "(no subject)",
                FromAddress = message.FromAddress ?? "",
                FromEmail = message.FromEmail ?? "",
                ToAddresses = message.ToAddresses ?? "",
                CcAddresses = message.CcAddresses ?? "",
                message.DateEpoch,
                Date = message.Date,
                Flags = message.Flags ?? "",
                Snippet = message.Snippet ?? "",
                message.HasAttachments,
                BodyText = message.BodyText,
                BodyHtml = message.BodyHtml,
                message.BodyFetched,
                message.ThreadId,
            });
        });

        // POST /api/messages/{accountId}/{folderId}/{uid}/fetch-body — Fetch message body on demand
        app.MapPost("/api/messages/{accountId}/{folderId:int}/{uid:long}/fetch-body", async (
            string accountId, int folderId, long uid,
            UltimateImapMcp.Core.Email.IEmailBackendFactory backendFactory,
            UltimateImapMcp.ImapClient.Repositories.FolderRepository folderRepo,
            UltimateImapMcp.ImapClient.Repositories.MessageRepository messageRepo) =>
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

                // Return updated message
                var message = messageRepo.GetByUid(accountId, folderId, uid);
                if (message is null)
                    return Results.NotFound(new { error = "Message not found after fetch" });

                return Results.Ok(new
                {
                    message.Id, message.Uid,
                    subject = message.Subject ?? "(no subject)",
                    bodyText = message.BodyText,
                    bodyHtml = message.BodyHtml,
                    bodyFetched = message.BodyFetched,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/messages/search?account_id=X&query=text&limit=20 — Full-text search
        app.MapGet("/api/messages/search", (HttpContext ctx, MessageRepository messageRepo, FolderRepository folderRepo) =>
        {
            var accountId = ctx.Request.Query["account_id"].FirstOrDefault();
            var query = ctx.Request.Query["query"].FirstOrDefault();
            if (string.IsNullOrEmpty(query))
                return Results.BadRequest(new { Error = "query is required" });

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
                m.Id,
                m.Uid,
                m.FolderId,
                Subject = m.Subject ?? "(no subject)",
                FromAddress = m.FromAddress ?? "",
                FromEmail = m.FromEmail ?? "",
                m.DateEpoch,
                Date = m.Date,
                Flags = m.Flags ?? "",
                Snippet = m.Snippet ?? "",
                m.HasAttachments,
                FolderPath = folderPaths.TryGetValue(m.FolderId, out var path) ? path : "",
            });

            return Results.Ok(result);
        });

        return app;
    }
}
