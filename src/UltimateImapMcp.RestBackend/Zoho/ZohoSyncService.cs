using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Coordination;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.RestBackend.Zoho;

/// <summary>
/// Background service that periodically polls Zoho REST accounts for new mail.
/// Runs independently of the IMAP SyncManager — only syncs accounts whose
/// backend_type is "zoho_rest".
/// </summary>
public sealed class ZohoSyncService : BackgroundService
{
    private readonly IEmailBackendFactory _backendFactory;
    private readonly AccountRepository _accountRepo;
    private readonly FolderRepository _folderRepo;
    private readonly IInstanceCoordinator _coordinator;
    private readonly ILogger<ZohoSyncService> _logger;

    /// <summary>Default polling interval for Zoho accounts.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);

    public ZohoSyncService(
        IEmailBackendFactory backendFactory,
        AccountRepository accountRepo,
        FolderRepository folderRepo,
        IInstanceCoordinator coordinator,
        ILogger<ZohoSyncService> logger)
    {
        _backendFactory = backendFactory;
        _accountRepo = accountRepo;
        _folderRepo = folderRepo;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait briefly for other services to initialize
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("ZohoSyncService started — looking for Zoho REST accounts");

        while (!ct.IsCancellationRequested)
        {
            if (!_coordinator.IsLeader)
            {
                try
                {
                    await Task.Delay(DefaultPollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            // Sync immediately on first iteration; delay at end of each cycle.
            // This is intentional: the leader syncs eagerly on startup, then waits
            // DefaultPollInterval before syncing again.
            try
            {
                await SyncAllZohoAccountsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ZohoSyncService: polling cycle failed, will retry next interval");
            }

            try
            {
                await Task.Delay(DefaultPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ZohoSyncService stopped");
    }

    private async Task SyncAllZohoAccountsAsync(CancellationToken ct)
    {
        var allAccounts = _accountRepo.GetAll();
        var zohoAccounts = allAccounts
            .Where(a => a.BackendType.Equals("zoho_rest", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (zohoAccounts.Count == 0)
        {
            _logger.LogDebug("ZohoSyncService: no Zoho REST accounts found, skipping");
            return;
        }

        _logger.LogInformation("ZohoSyncService: syncing {Count} Zoho account(s)", zohoAccounts.Count);

        foreach (var account in zohoAccounts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using var syncBackend = _backendFactory.CreateSyncBackend(account.Id);

                // Sync folders first
                await syncBackend.SyncFoldersAsync(account.Id, ct).ConfigureAwait(false);

                // Then sync messages for all enabled folders
                var folders = _folderRepo.GetByAccount(account.Id);
                var enabledFolders = folders.Where(f => f.SyncEnabled).ToList();

                _logger.LogInformation(
                    "ZohoSyncService: syncing {Count} enabled folder(s) for account {AccountName}",
                    enabledFolders.Count, account.Name);

                foreach (var folder in enabledFolders)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        await syncBackend.SyncFolderMessagesAsync(account.Id, folder.Path, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex,
                            "ZohoSyncService: failed to sync folder {Folder} for account {Account}",
                            folder.Path, account.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "ZohoSyncService: failed to sync account {Account}", account.Name);
            }
        }
    }
}
