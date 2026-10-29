using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.Dashboard;

public static class CacheApi
{
    /// <summary>Query value of <c>mode</c> on <c>DELETE /api/cache</c> that deletes the database files.</summary>
    public const string ResetDbMode = "reset-db";

    public static IEndpointRouteBuilder MapCacheApi(this IEndpointRouteBuilder app)
    {
        // Default: clear the cached mail in place, keep the server running, re-sync in the background.
        // ?mode=reset-db: delete the cache database files and stop the whole process (needs a restart).
        app.MapDelete("/api/cache", (HttpContext ctx, AppDatabase db, MessageRepository messageRepo,
            AccountRepository accountRepo, SyncManager syncManager, AppConfig config,
            RootLifetime rootLifetime, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            var mode = ctx.Request.Query["mode"].FirstOrDefault();

            if (string.Equals(mode, ResetDbMode, StringComparison.Ordinal))
                return ResetDatabase(db, syncManager, rootLifetime, logger);

            if (!string.IsNullOrEmpty(mode))
                return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Omit it, or use '{ResetDbMode}'." });

            try
            {
                // Cancel any in-flight sync so it is not half-way through a folder while the
                // cache is emptied. Remember whether the user had paused sync, and keep it paused.
                // If the user pauses sync while the clear runs, that pause wins.
                var wasPaused = syncManager.IsPaused;
                syncManager.StopSync();
                var ourPause = syncManager.PauseGeneration;

                CacheClearResult result;
                try
                {
                    result = messageRepo.ClearAll();
                }
                finally
                {
                    if (!wasPaused) syncManager.ResumeIfNotPausedSince(ourPause);
                }

                logger.LogInformation("Cache cleared: {Deleted} cached messages deleted", result.MessagesDeleted);

                var targets = accountRepo.GetAll()
                    .Where(a => a.Enabled)
                    .Select(a => (a.Id, (string?)null))
                    .ToList();
                var resync = ScheduleResync(syncManager, config, logger, targets, out var resyncNote);

                return Results.Ok(new
                {
                    deleted = result.MessagesDeleted,
                    resync,
                    message = $"Cleared {Messages(result.MessagesDeleted)} from the cache. {resyncNote}",
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache");
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapDelete("/api/cache/{accountId}", (string accountId, MessageRepository messageRepo,
            AccountRepository accountRepo, SyncManager syncManager, AppConfig config,
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
                var result = messageRepo.ClearAccount(account.Id);
                logger.LogInformation("Cache cleared for account {AccountId}: {Deleted} cached messages deleted",
                    account.Id, result.MessagesDeleted);

                bool resync;
                string resyncNote;
                if (!account.Enabled)
                {
                    resync = false;
                    resyncNote = "The account is disabled, so nothing will be re-downloaded until it is enabled.";
                }
                else
                {
                    resync = ScheduleResync(syncManager, config, logger, [(account.Id, null)], out resyncNote);
                }

                return Results.Ok(new
                {
                    deleted = result.MessagesDeleted,
                    accountId = account.Id,
                    resync,
                    message = $"Cleared {Messages(result.MessagesDeleted)} from the cache for {account.Name}. {resyncNote}",
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear cache for account {AccountId}", accountId);
                return Results.Json(new { error = $"Cache clear failed: {ex.Message}" }, statusCode: 500);
            }
        });

        app.MapDelete("/api/cache/{accountId}/{folderId:int}", (string accountId, int folderId,
            MessageRepository messageRepo, FolderRepository folderRepo, AccountRepository accountRepo,
            SyncManager syncManager, AppConfig config, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("CacheApi");
            if (string.IsNullOrWhiteSpace(accountId))
                return Results.BadRequest(new { error = "accountId is required" });

            var account = accountRepo.GetById(accountId);
            if (account is null)
                return Results.NotFound(new { error = $"Account '{accountId}' not found." });

            var folder = folderRepo.GetByAccount(account.Id).FirstOrDefault(f => f.Id == folderId);
            if (folder is null)
                return Results.NotFound(new { error = $"Folder {folderId} not found for account '{accountId}'." });

            try
            {
                var result = messageRepo.ClearFolder(account.Id, folder.Id);
                var stillCached = result.FolderLinksRemoved - result.MessagesDeleted;
                logger.LogInformation(
                    "Cache cleared for {AccountId}/{Folder}: {Removed} messages removed from the folder, {Deleted} deleted",
                    account.Id, folder.Path, result.FolderLinksRemoved, result.MessagesDeleted);

                bool resync;
                string resyncNote;
                if (!account.Enabled)
                {
                    resync = false;
                    resyncNote = "The account is disabled, so nothing will be re-downloaded until it is enabled.";
                }
                else if (!folder.SyncEnabled)
                {
                    resync = false;
                    resyncNote = "Sync is turned off for this folder, so it will not be re-downloaded.";
                }
                else
                {
                    resync = ScheduleResync(syncManager, config, logger, [(account.Id, folder.Path)], out resyncNote);
                }

                var summary = $"Removed {Messages(result.FolderLinksRemoved)} from the cache of {folder.DisplayName ?? folder.Path}.";
                if (stillCached > 0)
                    summary += $" {stillCached} of them stay cached because they are also in another folder.";

                return Results.Ok(new
                {
                    deleted = result.MessagesDeleted,
                    removedFromFolder = result.FolderLinksRemoved,
                    accountId = account.Id,
                    folderId = folder.Id,
                    resync,
                    message = $"{summary} {resyncNote}",
                });
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

    private static string Messages(int n) => n == 1 ? "1 message" : $"{n} messages";

    /// <summary>
    /// Deletes the cache database files and stops the whole process. Everything in cache.db is
    /// lost, including queued writes that have not reached the server, LLM analysis and usage
    /// history, LLM rules and dashboard sessions. The database is recreated on the next start,
    /// which has to happen by hand (or by the MCP client relaunching the server).
    /// </summary>
    private static IResult ResetDatabase(AppDatabase db, SyncManager syncManager,
        RootLifetime rootLifetime, ILogger logger)
    {
        try
        {
            syncManager.StopSync();
            db.DeleteDatabaseFiles();
            logger.LogWarning("Cache database files deleted. Stopping the server; it must be restarted.");
        }
        catch (Exception ex)
        {
            // DeleteDatabaseFiles closes the write connection before deleting, so a
            // failure part-way (e.g. another process holds the file on Windows) still
            // leaves this process unable to write. Stop rather than limp on.
            logger.LogError(ex, "Failed to delete the cache database; stopping the server");
            StopRootAfterResponse(rootLifetime, logger);
            return Results.Json(new
            {
                error = $"Cache reset failed: {ex.Message}. The server is stopping because its database " +
                        "connection was closed; restart it (some cache files may remain)."
            }, statusCode: 500);
        }

        StopRootAfterResponse(rootLifetime, logger);

        return Results.Ok(new
        {
            stopping = true,
            message = "Cache database deleted. The server is stopping and must be restarted; the database is recreated on the next start.",
        });
    }

    /// <summary>
    /// Stops the root host (the whole process, not just the dashboard web app) shortly
    /// after the current response has been sent.
    /// </summary>
    private static void StopRootAfterResponse(RootLifetime rootLifetime, ILogger logger)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                rootLifetime.StopApplication();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error stopping the server after cache reset");
            }
        });
    }

    /// <summary>
    /// Starts a background re-sync of the given accounts (or single folders) unless sync is
    /// paused or turned off in settings, in which case nothing is downloaded and the note says why.
    /// </summary>
    private static bool ScheduleResync(SyncManager syncManager, AppConfig config, ILogger logger,
        IReadOnlyList<(string AccountId, string? FolderPath)> targets, out string note)
    {
        if (syncManager.IsPaused)
        {
            note = "Sync is stopped, so nothing will be re-downloaded until you start it again.";
            return false;
        }
        if (!config.Sync.Enabled)
        {
            note = "Background sync is turned off in settings, so nothing will be re-downloaded until you sync manually.";
            return false;
        }
        if (targets.Count == 0)
        {
            note = "There are no enabled accounts to re-sync.";
            return false;
        }

        _ = Task.Run(async () =>
        {
            foreach (var (accountId, folderPath) in targets)
            {
                try
                {
                    await syncManager.TriggerSyncAsync(accountId, folderPath).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Re-sync after cache clear was cancelled for {AccountId}", accountId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Re-sync after cache clear failed for {AccountId} {Folder}",
                        accountId, folderPath ?? "(all folders)");
                }
            }
        });

        note = "Re-syncing in the background.";
        return true;
    }
}
