using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Encryption;
using UltimateImapMcp.Core.OAuth;
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
    AccountRepository accountRepo,
    CredentialEncryptor encryptor,
    ProviderProfileRegistry providerRegistry,
    ImapSyncService syncService,
    FolderRepository folderRepo,
    SyncLogRepository syncLogRepo,
    IOAuthAccessTokenProvider oauthProvider,
    IInstanceCoordinator coordinator,
    AppConfig appConfig,
    ILogger<SyncManager> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ImapConnectionManager> _connections = new();
    private readonly ConcurrentDictionary<string, FolderSyncStatus> _folderStatus = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _idleCts = new();
    private Action<string, object>? _onSyncEvent;

    /// <summary>
    /// Sets a callback to receive sync lifecycle events (sync:progress, sync:complete, sync:error).
    /// Typically wired to publish events to the dashboard IEventBus.
    /// </summary>
    public void SetEventCallback(Action<string, object> callback)
    {
        _onSyncEvent = callback;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dbAccounts = accountRepo.GetAll();

        logger.LogInformation("SyncManager started — {Count} account(s) in database",
            dbAccounts.Count);

        var tasks = new List<Task>();

        foreach (var record in dbAccounts)
        {
            var accountId = record.Id;
            var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

            // Create a connection manager for each account
            var connMgr = new ImapConnectionManager(account, encryptor,
                logger as ILogger<ImapConnectionManager>,
                oauthProvider, accountId);
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
            catch (Exception ex) { logger.LogDebug(ex, "Connection cleanup failed for account (non-fatal)"); }
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
            if (!coordinator.IsLeader || !appConfig.Sync.Enabled)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(appConfig.Server.HeartbeatInterval), idleCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            ImapClientLib? idleClient = null;
            try
            {
                idleClient = new ImapClientLib();
                await idleClient.ConnectAsync(
                    account.ImapHost, account.ImapPort,
                    SecureSocketOptions.SslOnConnect,
                    idleCts.Token).ConfigureAwait(false);

                if (account.AuthType.Equals("oauth2", StringComparison.OrdinalIgnoreCase)
                    && oauthProvider is not null)
                {
                    var accessToken = await oauthProvider.GetAccessTokenAsync(accountId, idleCts.Token)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"No OAuth access token available for IDLE connection to account '{account.Username}'.");
                    var oauth2 = new SaslMechanismOAuth2(account.Username, accessToken);
                    await idleClient.AuthenticateAsync(oauth2, idleCts.Token).ConfigureAwait(false);
                }
                else
                {
                    var password = account.Password ?? throw new InvalidOperationException($"No password configured for IDLE connection to account '{account.Username}'.");
                    await idleClient.AuthenticateAsync(
                        account.Username, password,
                        idleCts.Token).ConfigureAwait(false);
                }

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
                            logger.LogError(ex, "IDLE-triggered sync failed for {Account}/{Folder}",
                                accountId, folderPath);
                            _folderStatus[$"{accountId}:{folderPath}"] = new FolderSyncStatus(
                                folderPath, null, DateTime.UtcNow, 0, 0, "failed");
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
                try { idleClient?.Dispose(); } catch (Exception ex) { logger.LogDebug(ex, "IDLE client cleanup failed (non-fatal)"); }
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

        // Initial sync of all folders on startup — only the leader performs IMAP work.
        if (coordinator.IsLeader)
        {
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
                logger.LogError(ex, "Initial sync failed for {Account}", accountId);
            }
        }

        while (!ct.IsCancellationRequested)
        {
            if (!coordinator.IsLeader || !appConfig.Sync.Enabled)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

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
        await connMgr.ExecuteAsync(async client =>
        {
            // Sync folder list first
            var profile = providerRegistry.GetProfileByName(account.Provider);
            var mapper = new FolderMapper(profile);
            await syncService.SyncFoldersAsync(client, accountId, mapper, ct).ConfigureAwait(false);

            // Sync messages for each enabled folder
            var folders = folderRepo.GetByAccount(accountId);
            var enabledFolders = folders.Where(f => f.SyncEnabled).ToList();

            logger.LogInformation("Starting {SyncType} sync for {Account} — {Count} enabled folder(s)",
                syncType, accountId, enabledFolders.Count);

            foreach (var folder in enabledFolders)
            {
                ct.ThrowIfCancellationRequested();

                _folderStatus[$"{accountId}:{folder.Path}"] = new FolderSyncStatus(
                    folder.Path, folder.DisplayName, DateTime.UtcNow, folder.MessageCount, folder.UnreadCount, "syncing");

                _onSyncEvent?.Invoke("sync:progress", new { accountId, folder = folder.Path, status = "syncing", totalFolders = enabledFolders.Count });

                await SyncFolderInternalCoreAsync(client, accountId, folder, syncType, ct)
                    .ConfigureAwait(false);
            }

            logger.LogInformation("Completed {SyncType} sync for {Account} — {Count} folder(s) processed",
                syncType, accountId, enabledFolders.Count);
        }, ct).ConfigureAwait(false);
    }

    private async Task SyncFolderAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, string folderPath, string syncType,
        CancellationToken ct)
    {
        await connMgr.ExecuteAsync(async client =>
        {
            var folder = folderRepo.GetByPath(accountId, folderPath);
            if (folder is null)
            {
                // Folder not in DB yet — discover from IMAP
                logger.LogInformation("Folder {Folder} not in DB, discovering from IMAP for account {Account}",
                    folderPath, accountId);
                var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
                if (imapFolder is null)
                    throw new InvalidOperationException($"Folder '{folderPath}' does not exist on the IMAP server.");

                await imapFolder.OpenAsync(MailKit.FolderAccess.ReadOnly, ct).ConfigureAwait(false);
                var delimiter = imapFolder.DirectorySeparator.ToString();
                folderRepo.Insert(accountId, folderPath, imapFolder.Name, null, delimiter);
                await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);

                folder = folderRepo.GetByPath(accountId, folderPath)
                    ?? throw new InvalidOperationException($"Failed to create folder record for '{folderPath}'.");
            }

            await SyncFolderInternalCoreAsync(client, accountId, folder, syncType, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Syncs a single folder using ExecuteAsync for thread safety.</summary>
    private async Task SyncFolderInternalAsync(string accountId, ImapConnectionManager connMgr,
        FolderRecord folder, string syncType, CancellationToken ct)
    {
        await connMgr.ExecuteAsync(async client =>
        {
            await SyncFolderInternalCoreAsync(client, accountId, folder, syncType, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Core folder sync logic. Caller must ensure exclusive IMAP client access.</summary>
    private async Task SyncFolderInternalCoreAsync(MailKit.Net.Imap.ImapClient client,
        string accountId, FolderRecord folder, string syncType, CancellationToken ct)
    {
        var logId = syncLogRepo.LogStart(accountId, folder.Id, syncType);
        var sw = Stopwatch.StartNew();

        try
        {
            await syncService.SyncFolderMessagesAsync(client, accountId, folder,
                    cleanupServerDeleted: appConfig.Cache.CleanupServerDeleted, ct: ct)
                .ConfigureAwait(false);

            sw.Stop();

            var updatedFolder = folderRepo.GetByPath(accountId, folder.Path);
            var messagesSynced = updatedFolder is not null
                ? updatedFolder.LastSyncedUid - folder.LastSyncedUid
                : 0;

            syncLogRepo.LogComplete(logId, (int)Math.Max(0, messagesSynced), sw.ElapsedMilliseconds);

            _folderStatus[$"{accountId}:{folder.Path}"] = new FolderSyncStatus(
                folder.Path,
                folder.DisplayName,
                DateTime.UtcNow,
                updatedFolder?.MessageCount ?? folder.MessageCount,
                updatedFolder?.UnreadCount ?? folder.UnreadCount,
                "completed");

            _onSyncEvent?.Invoke("sync:complete", new { accountId, folder = folder.Path, messagesSynced = (int)Math.Max(0, messagesSynced), durationMs = sw.ElapsedMilliseconds });

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

            _onSyncEvent?.Invoke("sync:error", new { accountId, folder = folder.Path, error = ex.Message });

            logger.LogError(ex, "Sync failed for {Account}/{Folder}", accountId, folder.Path);

            // Rethrow connection-level errors so the parent stops trying more folders on a dead connection
            if (ex is IOException or SocketException or ImapProtocolException)
                throw;
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
        // Resolve account from DB
        var record = accountRepo.ResolveAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in database.");

        // Use the canonical DB id
        accountId = record.Id;
        var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

        // Create connection on the fly if not already tracked (e.g., account added after startup)
        var connMgr = _connections.GetOrAdd(accountId, _ =>
        {
            logger.LogInformation("Created on-demand connection for account {AccountId} ({Name})",
                accountId, record.Name);
            return new ImapConnectionManager(account, encryptor,
                logger as ILogger<ImapConnectionManager>,
                oauthProvider, accountId);
        });

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

    /// <summary>Searches directly on the IMAP server via connection manager.</summary>
    public async Task<List<MessageRecord>> ServerSearchAsync(
        string accountId, string folderPath, string? query, string? from,
        string? to, string? subject, long? fromEpoch, long? toEpoch,
        int maxResults, CancellationToken ct = default)
    {
        // Resolve account from DB (same pattern as TriggerSyncAsync)
        var record = accountRepo.ResolveAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in database.");

        accountId = record.Id;
        var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

        // Create connection on the fly if not already tracked (GetOrAdd is atomic)
        var connMgr = _connections.GetOrAdd(accountId, _ =>
        {
            logger.LogInformation("Created on-demand connection for server search on account {AccountId} ({Name})",
                accountId, record.Name);
            return new ImapConnectionManager(account, encryptor,
                logger as ILogger<ImapConnectionManager>,
                oauthProvider, accountId);
        });

        var folder = folderRepo.GetByPath(accountId, folderPath)
            ?? throw new InvalidOperationException($"Folder '{folderPath}' not found for account '{accountId}'.");

        return await connMgr.ExecuteAsync(async client =>
            await syncService.ServerSearchAsync(client, accountId, folder,
                query, from, to, subject, fromEpoch, toEpoch, maxResults, ct)
                .ConfigureAwait(false),
            ct).ConfigureAwait(false);
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
