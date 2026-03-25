using MailKit;
using Microsoft.Extensions.Logging;
using UltimateImapMcp.Core.Configuration;
using UltimateImapMcp.Core.Email;
using UltimateImapMcp.Core.Providers;
using UltimateImapMcp.ImapClient;
using UltimateImapMcp.ImapClient.Repositories;

namespace UltimateImapMcp.RestBackend.Imap;

/// <summary>
/// Wraps the existing IMAP sync code behind <see cref="IEmailSyncBackend"/>.
/// Delegates all calls to <see cref="ImapSyncService"/> and <see cref="ImapConnectionManager"/>.
/// </summary>
internal sealed class ImapSyncBackend : IEmailSyncBackend
{
    private readonly ImapConnectionManager _connMgr;
    private readonly ImapSyncService _syncService;
    private readonly FolderRepository _folderRepo;
    private readonly MessageRepository _messageRepo;
    private readonly ProviderProfileRegistry _providerRegistry;
    private readonly AccountConfig _accountConfig;
    private readonly ILogger<ImapSyncBackend> _logger;

    public ImapSyncBackend(
        ImapConnectionManager connMgr,
        ImapSyncService syncService,
        FolderRepository folderRepo,
        MessageRepository messageRepo,
        ProviderProfileRegistry providerRegistry,
        AccountConfig accountConfig,
        ILogger<ImapSyncBackend> logger)
    {
        _connMgr = connMgr;
        _syncService = syncService;
        _folderRepo = folderRepo;
        _messageRepo = messageRepo;
        _providerRegistry = providerRegistry;
        _accountConfig = accountConfig;
        _logger = logger;
    }

    public string BackendType => "imap";
    public bool SupportsRealtimeSync => true;

    public async Task SyncFoldersAsync(string accountId, CancellationToken ct = default)
    {
        await _connMgr.ExecuteAsync(async client =>
        {
            var profile = _providerRegistry.GetProfileByName(_accountConfig.Provider);
            var mapper = new FolderMapper(profile);
            await _syncService.SyncFoldersAsync(client, accountId, mapper, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SyncFolderMessagesAsync(string accountId, string folderPath,
        CancellationToken ct = default)
    {
        await _connMgr.ExecuteAsync(async client =>
        {
            var folder = _folderRepo.GetByPath(accountId, folderPath);
            if (folder is null)
            {
                _logger.LogWarning("Folder '{FolderPath}' not found in DB for account {AccountId}",
                    folderPath, accountId);
                return;
            }

            await _syncService.SyncFolderMessagesAsync(client, accountId, folder, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task FetchMessageBodyAsync(string accountId, string folderPath, long uid,
        CancellationToken ct = default)
    {
        await _connMgr.ExecuteAsync(async client =>
        {
            var folder = _folderRepo.GetByPath(accountId, folderPath);
            if (folder is null) return;

            var imapFolder = await client.GetFolderAsync(folderPath, ct).ConfigureAwait(false);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            try
            {
                var uidObj = new UniqueId((uint)uid);
                var message = await imapFolder.GetMessageAsync(uidObj, ct).ConfigureAwait(false);

                if (message is not null)
                {
                    var bodyText = message.TextBody;
                    var bodyHtml = message.HtmlBody;

                    var dbMessage = _messageRepo.GetByUid(accountId, folder.Id, uid);
                    if (dbMessage is not null)
                    {
                        _messageRepo.UpdateBody(dbMessage.Id, bodyText, bodyHtml);
                    }
                }
            }
            finally
            {
                await imapFolder.CloseAsync(false, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    public Task StartRealtimeListenerAsync(string accountId, string folderPath,
        Func<Task> onChangesDetected, CancellationToken ct = default)
    {
        // The real-time IDLE listener is managed by SyncManager directly.
        // This method exists for future refactoring when SyncManager delegates to backends.
        _logger.LogDebug(
            "IMAP realtime listener requested for {AccountId}/{Folder} — " +
            "currently managed by SyncManager directly", accountId, folderPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connMgr.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImapSyncBackend disconnect failed (non-fatal)");
        }
        _connMgr.Dispose();
    }
}
