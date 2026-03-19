using System.Collections.Concurrent;
using System.Diagnostics;
using MailKit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.ImapClient.Repositories;
using ImapClientLib = MailKit.Net.Imap.ImapClient;

namespace UltimateImapMcp.ImapClient;

/// <summary>
/// Background service that keeps the local cache fresh via three concurrent strategies:
/// 1. IDLE listeners for real-time updates on IDLE-enabled folders
/// 2. Polling loop for non-IDLE folders at configured intervals
/// 3. On-demand sync triggered by MCP tools
/// </summary>
public class SyncManager(
    AppConfig config,
    CredentialEncryptor encryptor,
    ProviderProfileRegistry providerRegistry,
    ImapSyncService syncService,
    FolderRepository folderRepo,
    SyncLogRepository syncLogRepo,
    ILogger<SyncManager> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ImapConnectionManager> _connections = new();
    private readonly ConcurrentDictionary<string, FolderSyncStatus> _folderStatus = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _idleCts = new();

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("SyncManager started — {Count} account(s) configured",
            config.Accounts.Count);

        var tasks = new List<Task>();

        foreach (var account in config.Accounts)
        {
            var accountId = account.Name.ToLowerInvariant().Replace(' ', '-');

            // Create a connection manager for each account
            var connMgr = new ImapConnectionManager(account, encryptor,
                logger as ILogger<ImapConnectionManager>);
            _connections[accountId] = connMgr;

            // Start IDLE listeners for configured folders
            foreach (var idleFolder in account.Sync.IdleFolders)
            {
                tasks.Add(RunIdleListenerAsync(accountId, account, connMgr, idleFolder, ct));
            }

            // Start polling loop for this account
            tasks.Add(RunPollingLoopAsync(accountId, account, connMgr, ct));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        // Cancel all IDLE listeners
        foreach (var (_, cts) in _idleCts)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _idleCts.Clear();

        // Disconnect all connections
        foreach (var (_, connMgr) in _connections)
        {
            try { await connMgr.DisconnectAsync(ct).ConfigureAwait(false); }
            catch { /* best-effort */ }
            connMgr.Dispose();
        }
        _connections.Clear();

        await base.StopAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // IDLE listener
    // ------------------------------------------------------------------

    private async Task RunIdleListenerAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, string folderPath, CancellationToken ct)
    {
        var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _idleCts[$"{accountId}:{folderPath}"] = idleCts;

        logger.LogInformation("Starting IDLE listener for {Account}/{Folder}",
            accountId, folderPath);

        // IDLE requires its own dedicated connection (separate from the shared one)
        while (!idleCts.Token.IsCancellationRequested)
        {
            ImapClientLib? idleClient = null;
            try
            {
                idleClient = new ImapClientLib();
                await idleClient.ConnectAsync(
                    account.ImapHost, account.ImapPort,
                    MailKit.Security.SecureSocketOptions.SslOnConnect,
                    idleCts.Token).ConfigureAwait(false);

                await idleClient.AuthenticateAsync(
                    account.Username, account.Password ?? string.Empty,
                    idleCts.Token).ConfigureAwait(false);

                var imapFolder = await idleClient.GetFolderAsync(folderPath, idleCts.Token)
                    .ConfigureAwait(false);
                await imapFolder.OpenAsync(FolderAccess.ReadOnly, idleCts.Token)
                    .ConfigureAwait(false);

                imapFolder.CountChanged += (_, _) =>
                {
                    // New messages arrived — trigger incremental sync
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SyncFolderAsync(accountId, account, connMgr, folderPath, "idle", ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "IDLE-triggered sync failed for {Account}/{Folder}",
                                accountId, folderPath);
                        }
                    }, ct);
                };

                // IDLE loop — MailKit recommends re-issuing IDLE every ~9 minutes
                // (IMAP servers typically drop IDLE after 30 minutes)
                while (!idleCts.Token.IsCancellationRequested)
                {
                    using var doneCts = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        idleCts.Token, doneCts.Token);

                    try
                    {
                        await idleClient.IdleAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (doneCts.IsCancellationRequested)
                    {
                        // 9-minute timeout — just re-issue IDLE
                    }
                }
            }
            catch (OperationCanceledException) when (idleCts.Token.IsCancellationRequested)
            {
                // Shutting down
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "IDLE listener for {Account}/{Folder} disconnected — reconnecting in 5s",
                    accountId, folderPath);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                try { idleClient?.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Polling loop
    // ------------------------------------------------------------------

    private async Task RunPollingLoopAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(account.Sync.PollInterval);

        logger.LogInformation("Starting polling loop for {Account} — interval {Interval}s",
            accountId, interval.TotalSeconds);

        // Initial sync of all folders on startup
        try
        {
            await SyncAllFoldersAsync(accountId, account, connMgr, "poll", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Initial sync failed for {Account}", accountId);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await SyncAllFoldersAsync(accountId, account, connMgr, "poll", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Polling sync failed for {Account} — will retry next interval",
                    accountId);
            }
        }
    }

    // ------------------------------------------------------------------
    // Sync operations
    // ------------------------------------------------------------------

    private async Task SyncAllFoldersAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, string syncType, CancellationToken ct)
    {
        var client = await connMgr.GetConnectedClientAsync(ct).ConfigureAwait(false);

        // Sync folder list first
        var profile = providerRegistry.GetProfileByName(account.Provider);
        var mapper = new FolderMapper(profile);
        await syncService.SyncFoldersAsync(client, accountId, mapper, ct).ConfigureAwait(false);

        // Sync messages for each enabled folder
        var folders = folderRepo.GetByAccount(accountId);
        foreach (var folder in folders.Where(f => f.SyncEnabled))
        {
            ct.ThrowIfCancellationRequested();
            await SyncFolderInternalAsync(accountId, connMgr, folder, syncType, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task SyncFolderAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, string folderPath, string syncType,
        CancellationToken ct)
    {
        var folder = folderRepo.GetByPath(accountId, folderPath);
        if (folder is null)
        {
            logger.LogWarning("Folder {Folder} not found for account {Account}", folderPath, accountId);
            return;
        }

        await SyncFolderInternalAsync(accountId, connMgr, folder, syncType, ct).ConfigureAwait(false);
    }

    private async Task SyncFolderInternalAsync(string accountId, ImapConnectionManager connMgr,
        FolderRecord folder, string syncType, CancellationToken ct)
    {
        var logId = syncLogRepo.LogStart(accountId, folder.Id, syncType);
        var sw = Stopwatch.StartNew();

        try
        {
            var client = await connMgr.GetConnectedClientAsync(ct).ConfigureAwait(false);
            await syncService.SyncFolderMessagesAsync(client, accountId, folder, ct)
                .ConfigureAwait(false);

            sw.Stop();

            // Get updated folder to see how many messages were synced
            var updatedFolder = folderRepo.GetByPath(accountId, folder.Path);
            var messagesSynced = updatedFolder is not null
                ? updatedFolder.LastSyncedUid - folder.LastSyncedUid
                : 0;

            syncLogRepo.LogComplete(logId, (int)Math.Max(0, messagesSynced), sw.ElapsedMilliseconds);

            // Update status
            _folderStatus[$"{accountId}:{folder.Path}"] = new FolderSyncStatus(
                folder.Path,
                folder.DisplayName,
                DateTime.UtcNow,
                updatedFolder?.MessageCount ?? folder.MessageCount,
                updatedFolder?.UnreadCount ?? folder.UnreadCount,
                "completed");

            logger.LogDebug("Synced {Account}/{Folder} — {Messages} new messages in {Duration}ms",
                accountId, folder.Path, messagesSynced, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            syncLogRepo.LogError(logId, ex.Message, sw.ElapsedMilliseconds);

            _folderStatus[$"{accountId}:{folder.Path}"] = new FolderSyncStatus(
                folder.Path,
                folder.DisplayName,
                DateTime.UtcNow,
                folder.MessageCount,
                folder.UnreadCount,
                "failed");

            logger.LogError(ex, "Sync failed for {Account}/{Folder}", accountId, folder.Path);
        }
    }

    // ------------------------------------------------------------------
    // Public API (for MCP tools)
    // ------------------------------------------------------------------

    /// <summary>
    /// Trigger an on-demand sync for a specific folder or all folders of an account.
    /// </summary>
    public async Task TriggerSyncAsync(string accountId, string? folderPath = null,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(accountId, out var connMgr))
        {
            throw new InvalidOperationException($"Account '{accountId}' is not configured or not connected.");
        }

        // Find the matching account config
        var account = config.Accounts.FirstOrDefault(a =>
            a.Name.ToLowerInvariant().Replace(' ', '-') == accountId)
            ?? throw new InvalidOperationException($"Account config for '{accountId}' not found.");

        if (folderPath is not null)
        {
            await SyncFolderAsync(accountId, account, connMgr, folderPath, "manual", ct)
                .ConfigureAwait(false);
        }
        else
        {
            await SyncAllFoldersAsync(accountId, account, connMgr, "manual", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns per-folder sync status for an account.
    /// </summary>
    public List<FolderSyncStatus> GetSyncStatus(string accountId)
    {
        var prefix = $"{accountId}:";
        return _folderStatus
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .OrderBy(s => s.FolderPath)
            .ToList();
    }
}

/// <summary>
/// Represents the current sync status of a single folder.
/// </summary>
public record FolderSyncStatus(
    string FolderPath,
    string? DisplayName,
    DateTime LastSyncedAt,
    int MessageCount,
    int UnreadCount,
    string Status);
