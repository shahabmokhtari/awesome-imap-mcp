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
    private readonly ConcurrentDictionary<string, bool> _hasPendingMessages = new();
    private Action<string, object>? _onSyncEvent;
    private volatile bool _paused;
    private volatile bool _isSyncing;

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

            // Skip disabled accounts — they should not sync
            if (!record.Enabled)
            {
                logger.LogInformation("Skipping sync for disabled account {Account} ({Name})", accountId, record.Name);
                continue;
            }

            var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

            // Skip REST-only accounts (e.g. Zoho OAuth) — they have no IMAP host
            // and are synced via their own REST backend (ZohoSyncService, etc.)
            if (string.IsNullOrEmpty(account.ImapHost))
            {
                logger.LogDebug("Skipping IMAP sync for {Account} — no IMAP host (REST-only account)", accountId);
                continue;
            }

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
        // Step 1: Cancel all IDLE listeners (signal them to stop)
        foreach (var (_, cts) in _idleCts)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Step 2: Wait for ExecuteAsync to finish (base.StopAsync cancels stoppingToken)
        await base.StopAsync(ct).ConfigureAwait(false);

        // Step 3: Now safe to dispose resources
        foreach (var (_, cts) in _idleCts)
        {
            try { cts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        _idleCts.Clear();

        foreach (var (_, connMgr) in _connections)
        {
            try { await connMgr.DisconnectAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogDebug(ex, "Connection cleanup failed (non-fatal)"); }
            try { connMgr.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        _connections.Clear();
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
        while (!ct.IsCancellationRequested)
        {
            if (!coordinator.IsLeader || !appConfig.Sync.Enabled)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(appConfig.Server.HeartbeatInterval), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            if (_paused)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
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
                while (!ct.IsCancellationRequested)
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break; // shutting down, don't reconnect

                logger.LogWarning(ex,
                    "IDLE listener for {Account}/{Folder} disconnected — reconnecting in 5s",
                    accountId, folderPath);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
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
                var remaining = await SyncAllFoldersAsync(accountId, account, connMgr, "poll", ct).ConfigureAwait(false);
                _hasPendingMessages[accountId] = remaining > 0;
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

            if (_paused)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Wait before next sync — short delay (2s) if still catching up, full interval if caught up
            var delay = _hasPendingMessages.GetValueOrDefault(accountId, false)
                ? TimeSpan.FromSeconds(2)
                : interval;

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _isSyncing = true;
                var remaining = await SyncAllFoldersAsync(accountId, account, connMgr, "poll", ct).ConfigureAwait(false);
                _hasPendingMessages[accountId] = remaining > 0;
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
            finally
            {
                _isSyncing = false;
            }
        }
    }

    // ------------------------------------------------------------------
    // Sync operations
    // ------------------------------------------------------------------

    /// <summary>Returns total remaining messages across all folders (0 = fully caught up).</summary>
    private async Task<int> SyncAllFoldersAsync(string accountId, AccountConfig account,
        ImapConnectionManager connMgr, string syncType, CancellationToken ct)
    {
        return await connMgr.ExecuteAsync(async client =>
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

            var totalRemaining = 0;
            foreach (var folder in enabledFolders)
            {
                ct.ThrowIfCancellationRequested();

                _folderStatus[$"{accountId}:{folder.Path}"] = new FolderSyncStatus(
                    folder.Path, folder.DisplayName, DateTime.UtcNow, folder.MessageCount, folder.UnreadCount, "syncing");

                _onSyncEvent?.Invoke("sync:progress", new { accountId, folder = folder.Path, status = "syncing", totalFolders = enabledFolders.Count });

                var remaining = await SyncFolderInternalCoreAsync(client, accountId, folder, syncType, ct)
                    .ConfigureAwait(false);
                totalRemaining += remaining;
            }

            logger.LogInformation("Completed {SyncType} sync for {Account} — {Count} folder(s) processed, {Remaining} messages still pending",
                syncType, accountId, enabledFolders.Count, totalRemaining);

            return totalRemaining;
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

    /// <summary>Core folder sync logic. Returns remaining messages. Caller must ensure exclusive IMAP client access.</summary>
    private async Task<int> SyncFolderInternalCoreAsync(MailKit.Net.Imap.ImapClient client,
        string accountId, FolderRecord folder, string syncType, CancellationToken ct)
    {
        var logId = syncLogRepo.LogStart(accountId, folder.Id, syncType);
        var sw = Stopwatch.StartNew();

        try
        {
            var remaining = await syncService.SyncFolderMessagesAsync(client, accountId, folder,
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

            logger.LogDebug("Synced {Account}/{Folder} — {Messages} new messages in {Duration}ms, {Remaining} remaining",
                accountId, folder.Path, messagesSynced, sw.ElapsedMilliseconds, remaining);

            return remaining;
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

            return 0; // On error, don't signal remaining — let the next cycle retry
        }
    }

    // ------------------------------------------------------------------
    // Public API (for MCP tools)
    // ------------------------------------------------------------------

    /// <summary>Whether sync is currently paused.</summary>
    public bool IsPaused => _paused;

    /// <summary>Whether any sync operation is currently running.</summary>
    public bool IsSyncing => _isSyncing;

    /// <summary>Pause all sync operations. Currently running syncs will complete.</summary>
    public void PauseSync()
    {
        _paused = true;
        logger.LogInformation("Sync paused");
    }

    /// <summary>Resume sync operations.</summary>
    public void ResumeSync()
    {
        _paused = false;
        logger.LogInformation("Sync resumed");
    }

    /// <summary>
    /// Trigger an on-demand sync for a specific folder or all folders of an account.
    /// </summary>
    public async Task TriggerSyncAsync(string accountId, string? folderPath = null,
        CancellationToken ct = default)
    {
        // Resolve account from DB — also checks enabled state
        var record = accountRepo.ResolveEnabledAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in database.");

        // Use the canonical DB id
        accountId = record.Id;
        var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

        // REST-only accounts (e.g. Zoho OAuth) have no IMAP host — they cannot be synced via IMAP
        if (string.IsNullOrEmpty(account.ImapHost))
        {
            logger.LogDebug("Skipping IMAP sync for {Account} — no IMAP host (REST-only account)", accountId);
            return;
        }

        // Create connection on the fly if not already tracked (e.g., account added after startup)
        var connMgr = _connections.GetOrAdd(accountId, _ =>
        {
            logger.LogInformation("Created on-demand connection for account {AccountId} ({Name})",
                accountId, record.Name);
            return new ImapConnectionManager(account, encryptor,
                logger as ILogger<ImapConnectionManager>,
                oauthProvider, accountId);
        });

        try
        {
            _isSyncing = true;
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
        finally
        {
            _isSyncing = false;
        }
    }

    /// <summary>Searches directly on the IMAP server via connection manager.</summary>
    public async Task<List<MessageRecord>> ServerSearchAsync(
        string accountId, string folderPath, string? query, string? from,
        string? to, string? subject, long? fromEpoch, long? toEpoch,
        int maxResults, CancellationToken ct = default)
    {
        // Resolve account from DB — also checks enabled state
        var record = accountRepo.ResolveEnabledAccount(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in database.");

        accountId = record.Id;
        var account = AccountConfigMapper.ToAccountConfig(record, encryptor);

        // REST-only accounts (e.g. Zoho OAuth) have no IMAP host — server search is not available
        if (string.IsNullOrEmpty(account.ImapHost))
            throw new InvalidOperationException(
                $"Server search is not available for REST-only account '{record.Name}' (no IMAP host).");

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
