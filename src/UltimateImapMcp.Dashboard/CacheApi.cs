using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.Dashboard;

public static class CacheApi
{
    public static IEndpointRouteBuilder MapCacheApi(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/cache", (MessageRepository messageRepo, FolderRepository folderRepo,
            SyncManager syncManager, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            var wasActive = !syncManager.IsPaused;
            try
            {
                if (wasActive) syncManager.PauseSync();
                var deleted = messageRepo.DeleteAll();
                folderRepo.ResetAllSyncState();
                return Results.Ok(new { deleted, message = "All cached messages cleared. Folders will re-sync." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear all cached messages");
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
            finally
            {
                if (wasActive) syncManager.ResumeSync();
            }
        });

        app.MapDelete("/api/cache/{accountId}", (string accountId, MessageRepository messageRepo,
            FolderRepository folderRepo, AccountRepository accountRepo, SyncManager syncManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            var account = accountRepo.GetById(accountId);
            if (account is null)
                return Results.NotFound(new { error = $"Account '{accountId}' not found." });

            var wasActive = !syncManager.IsPaused;
            try
            {
                if (wasActive) syncManager.PauseSync();
                var deleted = messageRepo.DeleteByAccount(accountId);
                folderRepo.ResetSyncState(accountId);
                return Results.Ok(new { deleted, accountId, message = "Cache cleared for account. Folders will re-sync." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId}", accountId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
            finally
            {
                if (wasActive) syncManager.ResumeSync();
            }
        });

        app.MapDelete("/api/cache/{accountId}/{folderId:int}", (string accountId, int folderId,
            MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            var wasActive = !syncManager.IsPaused;
            try
            {
                if (wasActive) syncManager.PauseSync();
                var deleted = messageRepo.DeleteByFolder(accountId, folderId);
                folderRepo.ResetFolderSyncState(accountId, folderId);
                return Results.Ok(new { deleted, accountId, folderId, message = "Cache cleared for folder. Folder will re-sync." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId} folder {FolderId}", accountId, folderId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
            finally
            {
                if (wasActive) syncManager.ResumeSync();
            }
        });

        app.MapGet("/api/cache/stats", (MessageRepository messageRepo, AccountRepository accountRepo,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                var stats = messageRepo.GetCacheStats();
                var byAccount = messageRepo.GetCacheStatsByAccount(accountRepo);
                return Results.Ok(new
                {
                    totalMessages = stats.TotalMessages,
                    bodiesFetched = stats.BodiesFetched,
                    dbSizeBytes = stats.DbSizeBytes,
                    dbSizeMb = Math.Round(stats.DbSizeBytes / (1024.0 * 1024.0), 2),
                    dbFreeSpaceBytes = stats.DbFreeSpaceBytes,
                    dbFreeSpaceMb = Math.Round(stats.DbFreeSpaceBytes / (1024.0 * 1024.0), 2),
                    accounts = byAccount.Select(a => new
                    {
                        a.AccountId,
                        a.AccountName,
                        a.MessageCount,
                        a.BodiesFetched,
                        a.OldestCachedAt,
                        a.NewestCachedAt
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("CacheApi");
                logger.LogError(ex, "Failed to get cache stats");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        return app;
    }
}
