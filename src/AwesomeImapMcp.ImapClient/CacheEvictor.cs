using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AwesomeImapMcp.Core.Configuration;
using AwesomeImapMcp.Core.Coordination;
using AwesomeImapMcp.Core.Database;
using AwesomeImapMcp.ImapClient.Repositories;

namespace AwesomeImapMcp.ImapClient;

/// <summary>
/// Background service that periodically evicts cached data to keep
/// the SQLite database within configured size and age limits.
/// Runs every 10 minutes. Eviction is done in batches of 500 rows.
/// </summary>
public class CacheEvictor(
    AppDatabase db,
    MessageRepository messageRepo,
    CacheConfig cacheConfig,
    IInstanceCoordinator coordinator,
    ILogger<CacheEvictor> logger) : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("CacheEvictor started — max_size_mb={MaxSize}, window_days={Window}, max_body_age_days={BodyAge}",
            cacheConfig.MaxSizeMb, cacheConfig.DefaultWindowDays, cacheConfig.MaxBodyAgeDays);

        while (!ct.IsCancellationRequested)
        {
            if (!coordinator.IsLeader)
            {
                try
                {
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            try
            {
                RunEviction();
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CacheEvictor encountered an error during eviction");

                try
                {
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Runs one eviction cycle. Exposed for testing.
    /// </summary>
    public void RunEviction()
    {
        // Size-based eviction
        var maxBytes = (long)cacheConfig.MaxSizeMb * 1024 * 1024;
        var dbFile = new FileInfo(db.DbPath);

        if (dbFile.Exists && dbFile.Length > maxBytes)
        {
            logger.LogInformation("DB size {SizeMb:F1}MB exceeds limit {LimitMb}MB — starting size-based eviction",
                dbFile.Length / (1024.0 * 1024.0), cacheConfig.MaxSizeMb);

            // First pass: evict bodies
            while (dbFile.Exists && dbFile.Length > maxBytes)
            {
                var evicted = messageRepo.EvictBodies(BatchSize);
                if (evicted == 0) break;
                logger.LogInformation("Evicted {Count} message bodies (size-based)", evicted);
                dbFile.Refresh();
            }

            // Second pass: delete oldest messages if still over limit
            while (dbFile.Exists && dbFile.Length > maxBytes)
            {
                var deleted = messageRepo.EvictMessages(BatchSize);
                if (deleted == 0) break;
                logger.LogInformation("Deleted {Count} messages (size-based)", deleted);
                dbFile.Refresh();
            }
        }

        // Time-based eviction (if configured)
        if (cacheConfig.MaxBodyAgeDays > 0)
        {
            var evicted = messageRepo.EvictBodiesOlderThan(cacheConfig.MaxBodyAgeDays);
            if (evicted > 0)
                logger.LogInformation("Evicted {Count} message bodies older than {Days} days (time-based)",
                    evicted, cacheConfig.MaxBodyAgeDays);
        }

        if (cacheConfig.DefaultWindowDays > 0)
        {
            var deleted = messageRepo.EvictMessagesOlderThan(cacheConfig.DefaultWindowDays);
            if (deleted > 0)
                logger.LogInformation("Deleted {Count} messages older than {Days} days (time-based)",
                    deleted, cacheConfig.DefaultWindowDays);
        }

        // Purge soft-deleted messages past retention
        if (cacheConfig.DeletedRetentionDays > 0)
        {
            var purged = messageRepo.PurgeSoftDeleted(cacheConfig.DeletedRetentionDays);
            if (purged > 0)
                logger.LogInformation("Purged {Count} soft-deleted messages (retention: {Days} days)",
                    purged, cacheConfig.DeletedRetentionDays);
        }
    }
}
