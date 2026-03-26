using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class CacheApi
{
    public static IEndpointRouteBuilder MapCacheApi(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/cache", (MessageRepository messageRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            try
            {
                var deleted = messageRepo.DeleteAll();
                return Results.Ok(new { deleted, message = "All cached messages cleared." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear all cached messages");
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapDelete("/api/cache/{accountId}", (string accountId, MessageRepository messageRepo,
            AccountRepository accountRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            var account = accountRepo.GetById(accountId);
            if (account is null)
                return Results.NotFound(new { error = $"Account '{accountId}' not found." });

            try
            {
                var deleted = messageRepo.DeleteByAccount(accountId);
                return Results.Ok(new { deleted, accountId, message = "Cache cleared for account." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId}", accountId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapDelete("/api/cache/{accountId}/{folderId:int}", (string accountId, int folderId,
            MessageRepository messageRepo, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            try
            {
                var deleted = messageRepo.DeleteByFolder(accountId, folderId);
                return Results.Ok(new { deleted, accountId, folderId, message = "Cache cleared for folder." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId} folder {FolderId}", accountId, folderId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        return app;
    }
}
