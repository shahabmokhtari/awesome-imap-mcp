using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.Dashboard;

public static class CacheApi
{
    public static IEndpointRouteBuilder MapCacheApi(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/cache", (AppDatabase db, SyncManager syncManager,
            IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            try
            {
                // Stop all sync operations
                syncManager.StopSync();

                // Delete the DB files — they'll be recreated on next startup
                db.DeleteDatabaseFiles();
                logger.LogInformation("Cache database deleted. Server will restart.");

                // Trigger graceful shutdown — MCP host will restart the process
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500); // give response time to send
                    lifetime.StopApplication();
                });

                return Results.Ok(new { message = "Cache cleared. Server restarting..." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache");
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
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

            try
            {
                syncManager.StopSync();
                var deleted = messageRepo.DeleteByAccount(accountId);
                folderRepo.ResetSyncState(accountId);
                return Results.Ok(new { deleted, accountId, message = "Cache cleared for account. Use Start Sync to resume." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId}", accountId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapDelete("/api/cache/{accountId}/{folderId:int}", (string accountId, int folderId,
            MessageRepository messageRepo, FolderRepository folderRepo, SyncManager syncManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            try
            {
                syncManager.StopSync();
                var deleted = messageRepo.DeleteByFolder(accountId, folderId);
                folderRepo.ResetFolderSyncState(accountId, folderId);
                return Results.Ok(new { deleted, accountId, folderId, message = "Cache cleared for folder. Use Start Sync to resume." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId} folder {FolderId}", accountId, folderId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
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
